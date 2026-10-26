// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.EntityFrameworkCore;
using OpenEleven.Data;
using OpenEleven.Data.Entities;
using OpenEleven.Protocol.Kv;
using OpenEleven.Server.Configuration;
using OpenEleven.Server.Dispatch;
using OpenEleven.Server.State;

namespace OpenEleven.Server.Handlers;

/// <summary>
/// Short mail: send, inbox and sent lists (FUN_005f4e10 / FUN_005f45a0), reading one
/// (FUN_005e87e0 / FUN_005e7f50) and deleting. mail_type is enum 0x23 (always MAIL_NORMAL
/// here), reply_enable and read_flag are YES|NO, dates are unix seconds.
/// </summary>
public sealed class MailHandlers(GameDbContext db)
{
    // PES2010 sends the mail commands on the Lobby socket; PES2011 uses Menu.
    private const ServiceRole Roles = ServiceRole.Menu | ServiceRole.Lobby;

    /// <summary>Mails kept per inbox; the oldest go first.</summary>
    public const int MaxInbox = 50;

    /// <summary>Client buffers: subject 192 bytes, body 1536 (FUN_005e87e0).</summary>
    private const int MaxSubject = 191, MaxBody = 1535;

    [Command("CMD_SEND_SHORTMAIL", Roles = Roles, RequiredState = SessionState.PlayerSelected)]
    public async ValueTask<KvMessage[]> Send(CommandContext ctx)
    {
        var me = ctx.Session.Pid;
        var to = ctx.Request.GetInt32("to_pid", 0);
        // ERR_NOTFOUNDCLIENT is the only refusal the client knows; a block looks the same.
        if (to == me || await db.Players.FindAsync([to], ctx.CancellationToken) is null
            || await db.Blacklist.AnyAsync(b => b.Pid == to && b.TargetPid == me, ctx.CancellationToken))
            return [ctx.Fail("ERR_NOTFOUNDCLIENT")];

        db.ShortMails.Add(new ShortMail
        {
            FromPid = me,
            ToPid = to,
            Subject = Clip(ctx.Request.GetString("mes_subject"), MaxSubject),
            Body = Clip(ctx.Request.GetString("mes_body"), MaxBody),
        });
        await db.SaveChangesAsync(ctx.CancellationToken);

        // ponytail: inbox cap enforced on send; a periodic sweep if inboxes get large.
        var overflow = await db.ShortMails.Where(m => m.ToPid == to && !m.DeletedByRecipient)
            .OrderByDescending(m => m.CreatedAt).Skip(MaxInbox).ToListAsync(ctx.CancellationToken);
        foreach (var old in overflow)
            Delete(old, recipient: true);
        await db.SaveChangesAsync(ctx.CancellationToken);
        return [ctx.Ok()];
    }

    [Command("CMD_GET_SHORTMAILLIST", Roles = Roles, RequiredState = SessionState.PlayerSelected)]
    public async ValueTask<KvMessage[]> Inbox(CommandContext ctx)
    {
        var mails = await db.ShortMails.Where(m => m.ToPid == ctx.Session.Pid && !m.DeletedByRecipient)
            .OrderByDescending(m => m.CreatedAt).Take(MaxInbox).ToListAsync(ctx.CancellationToken);
        var names = await Names(mails.Select(m => m.FromPid), ctx.CancellationToken);
        var entries = mails.Select(m => new KvMessage()
            .Set("smail_id", m.Id)
            .Set("from_pid", m.FromPid)
            .Set("from_name", names.GetValueOrDefault(m.FromPid, ""))
            .Set("mail_type", "MAIL_NORMAL")
            .Set("reply_enable", "YES")
            .Set("read_flag", m.Read ? "YES" : "NO")
            .Set("date", RankSystem.Unix(m.CreatedAt))
            .Set("mes_subject", m.Subject)).ToArray();
        return [ctx.Ok().SetList("smail_num", "smlist", entries)];
    }

    [Command("CMD_GET_SENDSHORTMAILLIST", Roles = Roles, RequiredState = SessionState.PlayerSelected)]
    public async ValueTask<KvMessage[]> Sent(CommandContext ctx)
    {
        var mails = await db.ShortMails.Where(m => m.FromPid == ctx.Session.Pid && !m.DeletedBySender)
            .OrderByDescending(m => m.CreatedAt).Take(MaxInbox).ToListAsync(ctx.CancellationToken);
        var names = await Names(mails.Select(m => m.ToPid), ctx.CancellationToken);
        var entries = mails.Select(m => new KvMessage()
            .Set("smail_id", m.Id)
            .Set("to_pid", m.ToPid)
            .Set("to_name", names.GetValueOrDefault(m.ToPid, ""))
            .Set("date", RankSystem.Unix(m.CreatedAt))
            .Set("mes_subject", m.Subject)).ToArray();
        return [ctx.Ok().SetList("smail_num", "smlist", entries)];
    }

    /// <summary>Opening an inbox mail marks it read.</summary>
    [Command("CMD_GET_SHORTMAIL", Roles = Roles, RequiredState = SessionState.PlayerSelected)]
    public async ValueTask<KvMessage[]> Read(CommandContext ctx)
    {
        var id = ctx.Request.GetInt32("smail_id", 0);
        var mail = await db.ShortMails.FirstOrDefaultAsync(m => m.Id == id && m.ToPid == ctx.Session.Pid && !m.DeletedByRecipient, ctx.CancellationToken);
        if (mail is null)
            return [ctx.Fail("ERR_DATABASE")];

        mail.Read = true;
        await db.SaveChangesAsync(ctx.CancellationToken);
        var from = await db.Players.FindAsync([mail.FromPid], ctx.CancellationToken);
        return
        [
            ctx.Ok()
                .Set("from_pid", mail.FromPid)
                .Set("from_name", from?.Name ?? "")
                .Set("mail_type", "MAIL_NORMAL")
                .Set("reply_enable", "YES")
                .Set("date", RankSystem.Unix(mail.CreatedAt))
                .Set("mes_subject", mail.Subject)
                .Set("mes_body", mail.Body),
        ];
    }

    [Command("CMD_GET_SENDSHORTMAIL", Roles = Roles, RequiredState = SessionState.PlayerSelected)]
    public async ValueTask<KvMessage[]> ReadSent(CommandContext ctx)
    {
        var id = ctx.Request.GetInt32("smail_id", 0);
        var mail = await db.ShortMails.FirstOrDefaultAsync(m => m.Id == id && m.FromPid == ctx.Session.Pid && !m.DeletedBySender, ctx.CancellationToken);
        if (mail is null)
            return [ctx.Fail("ERR_DATABASE")];

        var to = await db.Players.FindAsync([mail.ToPid], ctx.CancellationToken);
        return
        [
            ctx.Ok()
                .Set("to_pid", mail.ToPid)
                .Set("to_name", to?.Name ?? "")
                .Set("date", RankSystem.Unix(mail.CreatedAt))
                .Set("mes_subject", mail.Subject)
                .Set("mes_body", mail.Body),
        ];
    }

    [Command("CMD_DEL_SHORTMAIL", Roles = Roles, RequiredState = SessionState.PlayerSelected)]
    public ValueTask<KvMessage[]> DeleteReceived(CommandContext ctx) => DeleteAsync(ctx, recipient: true);

    [Command("CMD_DEL_SENDSHORTMAIL", Roles = Roles, RequiredState = SessionState.PlayerSelected)]
    public ValueTask<KvMessage[]> DeleteSent(CommandContext ctx) => DeleteAsync(ctx, recipient: false);

    /// <summary>New-mail subscription (FUN_005213d0). Parked: a bare reply would count as a delivery.</summary>
    [Command("CMD_WATCH_SHORTMAIL", Roles = Roles)]
    public ValueTask<KvMessage[]> Watch(CommandContext ctx) => Reply.None();

    /// <summary>mailcount / newmail for CMD_SET_CURRENTPLAYER: inbox size and whether any is unread.</summary>
    public static async Task<(int Count, bool Unread)> InboxStateAsync(GameDbContext db, int pid, CancellationToken ct)
    {
        var inbox = db.ShortMails.Where(m => m.ToPid == pid && !m.DeletedByRecipient);
        return (await inbox.CountAsync(ct), await inbox.AnyAsync(m => !m.Read, ct));
    }

    private async ValueTask<KvMessage[]> DeleteAsync(CommandContext ctx, bool recipient)
    {
        var id = ctx.Request.GetInt32("smail_id", 0);
        var me = ctx.Session.Pid;
        var mail = await db.ShortMails.FirstOrDefaultAsync(m => m.Id == id && (recipient ? m.ToPid == me : m.FromPid == me), ctx.CancellationToken);
        if (mail is not null)
        {
            Delete(mail, recipient);
            await db.SaveChangesAsync(ctx.CancellationToken);
        }

        return [ctx.Ok()];
    }

    private void Delete(ShortMail mail, bool recipient)
    {
        if (recipient) mail.DeletedByRecipient = true;
        else mail.DeletedBySender = true;
        if (mail.DeletedByRecipient && mail.DeletedBySender)
            db.ShortMails.Remove(mail);
    }

    private async Task<Dictionary<int, string>> Names(IEnumerable<int> pids, CancellationToken ct)
    {
        var ids = pids.Distinct().ToList();
        return await db.Players.Where(p => ids.Contains(p.Pid)).ToDictionaryAsync(p => p.Pid, p => p.Name, ct);
    }

    private static string Clip(string? text, int max)
    {
        text ??= "";
        while (System.Text.Encoding.UTF8.GetByteCount(text) > max)
            text = text[..^1];
        return text;
    }
}
