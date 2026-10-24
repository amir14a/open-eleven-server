// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.EntityFrameworkCore;
using OpenEleven.Data.Entities;

namespace OpenEleven.Data;

public sealed class GameDbContext(DbContextOptions<GameDbContext> options) : DbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Player> Players => Set<Player>();
    public DbSet<PlayerStats> PlayerStats => Set<PlayerStats>();
    public DbSet<MatchRecord> Matches => Set<MatchRecord>();
    public DbSet<InformationItem> Information => Set<InformationItem>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Account>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.GameId).IsUnique();

            // Shared product serials are valid. This is an administrative index only.
            e.HasIndex(x => x.RegCode);

            e.HasMany(x => x.Players)
                .WithOne(x => x.Account!)
                .HasForeignKey(x => x.AccountId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Player>(e =>
        {
            e.HasKey(x => x.Pid);
            e.HasIndex(x => x.Name).IsUnique();
            e.HasIndex(x => x.NormalizedName).IsUnique();
            e.HasIndex(x => x.AccountId).IsUnique();
            e.HasOne(x => x.Stats)
                .WithOne()
                .HasForeignKey<PlayerStats>(x => x.PlayerId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<PlayerStats>(e => e.HasKey(x => x.Id));

        b.Entity<MatchRecord>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.HomePid);
            e.HasIndex(x => x.AwayPid);
            e.HasIndex(x => x.PlayedAt);
        });

        b.Entity<InformationItem>(e => e.HasKey(x => x.Id));
    }
}
