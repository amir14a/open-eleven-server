// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

namespace OpenEleven.Server.Configuration;

/// <summary>
/// Texts the server writes itself, in the client's language. Language codes are pes2011.exe
/// enum 0x1c (EN FR GR IT SP JP KR US PR MX RU HE SV NL BR TR CH ANY); the client sends its
/// own in the gate requests (lang) and each profile keeps one (Player.Lang).
/// </summary>
/// <remarks>
/// Every language of the enum is translated (CH as Traditional Chinese); unknown codes and ANY
/// fall back to English. Texts travel as UTF-8 (see InnerBody). Variants share a text:
/// US = EN, MX = SP, BR = PR. Placeholders are {0}, {1}...
/// </remarks>
public static class ServerText
{
    public static string Get(string key, string? lang, params object[] args)
    {
        var code = (lang ?? "EN").Trim().ToUpperInvariant() switch
        {
            "US" => "EN",
            "MX" => "SP",
            "BR" => "PR",
            var other => other,
        };

        var texts = Texts[key];
        var text = texts.GetValueOrDefault(code) ?? texts["EN"];
        return args.Length == 0 ? text : string.Format(text, args);
    }

    private static readonly Dictionary<string, Dictionary<string, string>> Texts = new()
    {
        // -------------------------------------------------------------------------------------
        // PROJECT NOTICE: please do not change or remove about.subject / about.body.
        //
        // This in-game notice is the project's author attribution and licence notice, shown to
        // every player. If you want to change it, please open an issue in the OpenEleven GitHub
        // repository first and ask the maintainers.
        //
        // The code itself is AGPL v3: you may use, study, modify and redistribute it, and anyone
        // who runs a modified server for players must offer them its source (section 13). The
        // licence also requires keeping the copyright and legal notices intact (sections 4 and
        // 5), and section 7(b) allows requiring that author attributions like this one be
        // preserved. Removing or misrepresenting them may breach the licence.
        // -------------------------------------------------------------------------------------
        ["about.subject"] = new()
        {
            ["EN"] = "About OpenEleven",
            ["FR"] = "À propos d'OpenEleven",
            ["GR"] = "Über OpenEleven",
            ["IT"] = "Informazioni su OpenEleven",
            ["SP"] = "Acerca de OpenEleven",
            ["PR"] = "Sobre o OpenEleven",
            ["NL"] = "Over OpenEleven",
            ["SV"] = "Om OpenEleven",
            ["TR"] = "OpenEleven hakkında",
            ["JP"] = "OpenElevenについて",
            ["KR"] = "OpenEleven 정보",
            ["RU"] = "Об OpenEleven",
            ["HE"] = "אודות OpenEleven",
            ["CH"] = "關於 OpenEleven",
        },
        ["about.body"] = new()
        {
            ["EN"] = "OpenEleven is an independent, non-commercial fan project. It is free and open source " +
                     "software under the GNU AGPL v3: you may use, study, share and modify it, and anyone who " +
                     "runs a modified server must offer its source code to its players.\n" +
                     "It is not affiliated with or endorsed by Konami.\n\n\n" +
                     "Server: PES5 Indie Team.\n" +
                     "Reverse engineering: marqisspes5 and contributors.\n" +
                     "Thanks to everyone keeping these games online.",
            ["FR"] = "OpenEleven est un projet de fans indépendant et non commercial. C'est un logiciel libre " +
                     "sous licence GNU AGPL v3 : vous pouvez l'utiliser, l'étudier, le partager et le modifier, " +
                     "et quiconque exploite un serveur modifié doit en proposer le code source à ses joueurs.\n" +
                     "Il n'est ni affilié à Konami ni approuvé par Konami.\n\n\n" +
                     "Serveur : PES5 Indie Team.\n" +
                     "Rétro-ingénierie : marqisspes5 et contributeurs.\n" +
                     "Merci à tous ceux qui font vivre ces jeux en ligne.",
            ["GR"] = "OpenEleven ist ein unabhängiges, nicht kommerzielles Fanprojekt. Es ist freie Software " +
                     "unter der GNU AGPL v3: Du darfst sie nutzen, untersuchen, weitergeben und verändern, und wer " +
                     "einen veränderten Server betreibt, muss seinen Spielern den Quellcode anbieten.\n" +
                     "Es steht in keiner Verbindung zu Konami und wird nicht von Konami unterstützt.\n\n\n" +
                     "Server: PES5 Indie Team.\n" +
                     "Reverse Engineering: marqisspes5 und Mitwirkende.\n" +
                     "Danke an alle, die diese Spiele online halten.",
            ["IT"] = "OpenEleven è un progetto amatoriale indipendente e senza scopo di lucro. È software libero " +
                     "con licenza GNU AGPL v3: puoi usarlo, studiarlo, condividerlo e modificarlo, e chi gestisce " +
                     "un server modificato deve offrirne il codice sorgente ai propri giocatori.\n" +
                     "Non è affiliato né approvato da Konami.\n\n\n" +
                     "Server: PES5 Indie Team.\n" +
                     "Reverse engineering: marqisspes5 e collaboratori.\n" +
                     "Grazie a tutti quelli che mantengono questi giochi online.",
            ["SP"] = "OpenEleven es un proyecto de fans independiente y sin fines comerciales. Es software libre " +
                     "bajo la licencia GNU AGPL v3: puedes usarlo, estudiarlo, compartirlo y modificarlo, y quien " +
                     "opere un servidor modificado debe ofrecer su código fuente a sus jugadores.\n" +
                     "No está afiliado ni respaldado por Konami.\n\n\n" +
                     "Servidor: PES5 Indie Team.\n" +
                     "Ingeniería inversa: marqisspes5 y colaboradores.\n" +
                     "Gracias a todos los que mantienen estos juegos online.",
            ["PR"] = "O OpenEleven é um projeto de fãs independente e sem fins comerciais. É software livre sob a " +
                     "licença GNU AGPL v3: você pode usá-lo, estudá-lo, compartilhá-lo e modificá-lo, e quem " +
                     "opera um servidor modificado deve oferecer o código-fonte aos seus jogadores.\n" +
                     "Não é afiliado nem endossado pela Konami.\n\n\n" +
                     "Servidor: PES5 Indie Team.\n" +
                     "Engenharia reversa: marqisspes5 e colaboradores.\n" +
                     "Obrigado a todos que mantêm esses jogos online.",
            ["NL"] = "OpenEleven is een onafhankelijk, niet-commercieel fanproject. Het is vrije software onder de " +
                     "GNU AGPL v3: je mag het gebruiken, bestuderen, delen en aanpassen, en wie een aangepaste " +
                     "server draait, moet de broncode aan zijn spelers aanbieden.\n" +
                     "Het is niet verbonden aan of goedgekeurd door Konami.\n\n\n" +
                     "Server: PES5 Indie Team.\n" +
                     "Reverse engineering: marqisspes5 en bijdragers.\n" +
                     "Dank aan iedereen die deze games online houdt.",
            ["SV"] = "OpenEleven är ett oberoende, icke-kommersiellt fanprojekt. Det är fri programvara under " +
                     "GNU AGPL v3: du får använda, studera, dela och ändra den, och den som driver en ändrad server " +
                     "måste erbjuda källkoden till sina spelare.\n" +
                     "Det är inte knutet till eller godkänt av Konami.\n\n\n" +
                     "Server: PES5 Indie Team.\n" +
                     "Reverse engineering: marqisspes5 och bidragsgivare.\n" +
                     "Tack till alla som håller dessa spel online.",
            ["TR"] = "OpenEleven bağımsız ve ticari olmayan bir hayran projesidir. GNU AGPL v3 lisanslı özgür " +
                     "yazılımdır: kullanabilir, inceleyebilir, paylaşabilir ve değiştirebilirsiniz; değiştirilmiş " +
                     "bir sunucu işleten herkes kaynak kodunu oyuncularına sunmak zorundadır.\n" +
                     "Konami ile bağlantılı değildir ve Konami tarafından onaylanmamıştır.\n\n\n" +
                     "Sunucu: PES5 Indie Team.\n" +
                     "Tersine mühendislik: marqisspes5 ve katkıda bulunanlar.\n" +
                     "Bu oyunları çevrimiçi tutan herkese teşekkürler.",
            ["JP"] = "OpenElevenは独立した非営利のファンプロジェクトです。GNU AGPL v3に基づくフリーでオープンソースの" +
                     "ソフトウェアで、自由に使用・研究・共有・改変できます。改変したサーバーを運営する場合は、" +
                     "そのソースコードをプレイヤーに提供する必要があります。\n" +
                     "Konamiとは提携しておらず、Konamiの承認も受けていません。\n\n\n" +
                     "サーバー: PES5 Indie Team\n" +
                     "リバースエンジニアリング: marqisspes5 と協力者の皆さん\n" +
                     "これらのゲームをオンラインで支え続けるすべての方に感謝します。",
            ["KR"] = "OpenEleven은 독립적인 비상업적 팬 프로젝트입니다. GNU AGPL v3 라이선스의 자유 오픈 소스 " +
                     "소프트웨어로, 누구나 사용, 연구, 공유, 수정할 수 있으며 수정된 서버를 운영하는 사람은 " +
                     "플레이어에게 소스 코드를 제공해야 합니다.\n" +
                     "Konami와 제휴하거나 Konami의 승인을 받지 않았습니다.\n\n\n" +
                     "서버: PES5 Indie Team\n" +
                     "리버스 엔지니어링: marqisspes5 및 기여자들\n" +
                     "이 게임들을 온라인으로 유지해 주시는 모든 분께 감사드립니다.",
            ["RU"] = "OpenEleven — независимый некоммерческий фанатский проект. Это свободное программное " +
                     "обеспечение под лицензией GNU AGPL v3: вы можете использовать, изучать, распространять и " +
                     "изменять его, а тот, кто запускает изменённый сервер, обязан предоставить игрокам его исходный код.\n" +
                     "Проект не связан с Konami и не одобрен Konami.\n\n\n" +
                     "Сервер: PES5 Indie Team.\n" +
                     "Реверс-инжиниринг: marqisspes5 и участники.\n" +
                     "Спасибо всем, кто поддерживает эти игры в сети.",
            ["HE"] = "OpenEleven הוא פרויקט מעריצים עצמאי ולא מסחרי. זוהי תוכנה חופשית בקוד פתוח תחת רישיון " +
                     "GNU AGPL v3: מותר להשתמש בה, ללמוד אותה, לשתף ולשנות אותה, וכל מי שמפעיל שרת ששונה חייב " +
                     "להציע לשחקנים את קוד המקור שלו.\n" +
                     "הפרויקט אינו קשור ל-Konami ואינו מאושר על ידה.\n\n\n" +
                     "שרת: PES5 Indie Team.\n" +
                     "הנדסה לאחור: marqisspes5 ותורמים.\n" +
                     "תודה לכל מי ששומר על המשחקים האלה ברשת.",
            ["CH"] = "OpenEleven 是一個獨立、非商業性的玩家專案。它是採用 GNU AGPL v3 授權的自由開源軟體：" +
                     "你可以使用、研究、分享和修改它，任何運行修改版伺服器的人都必須向其玩家提供原始碼。\n" +
                     "本專案與 Konami 無關，也未獲得 Konami 認可。\n\n\n" +
                     "伺服器：PES5 Indie Team\n" +
                     "逆向工程：marqisspes5 及貢獻者\n" +
                     "感謝所有讓這些遊戲持續在線的人。",
        },
        ["eula.text"] = new()
        {
            ["EN"] = "OpenEleven is an independent, non-commercial fan server, not affiliated with Konami. " +
                     "Be respectful to other players. Cheating, abuse and modified game data lead to a ban.",
            ["FR"] = "OpenEleven est un serveur de fans indépendant et non commercial, sans lien avec Konami. " +
                     "Respectez les autres joueurs. La triche, les abus et les données de jeu modifiées entraînent un bannissement.",
            ["GR"] = "OpenEleven ist ein unabhängiger, nicht kommerzieller Fanserver ohne Verbindung zu Konami. " +
                     "Sei respektvoll zu anderen Spielern. Cheaten, Missbrauch und veränderte Spieldaten führen zu einer Sperre.",
            ["IT"] = "OpenEleven è un server amatoriale indipendente e senza scopo di lucro, non affiliato a Konami. " +
                     "Rispetta gli altri giocatori. Trucchi, abusi e dati di gioco modificati comportano il ban.",
            ["SP"] = "OpenEleven es un servidor de fans independiente y sin fines comerciales, no afiliado a Konami. " +
                     "Respeta a los demás jugadores. Las trampas, los abusos y los datos de juego modificados se sancionan con un baneo.",
            ["PR"] = "O OpenEleven é um servidor de fãs independente e sem fins comerciais, não afiliado à Konami. " +
                     "Respeite os outros jogadores. Trapaças, abusos e dados de jogo modificados levam ao banimento.",
            ["NL"] = "OpenEleven is een onafhankelijke, niet-commerciële fanserver, niet verbonden aan Konami. " +
                     "Respecteer andere spelers. Valsspelen, misbruik en aangepaste speldata leiden tot een ban.",
            ["SV"] = "OpenEleven är en oberoende, icke-kommersiell fanserver utan koppling till Konami. " +
                     "Visa respekt mot andra spelare. Fusk, missbruk och ändrad speldata leder till avstängning.",
            ["TR"] = "OpenEleven, Konami ile bağlantısı olmayan bağımsız ve ticari olmayan bir hayran sunucusudur. " +
                     "Diğer oyunculara saygılı olun. Hile, kötüye kullanım ve değiştirilmiş oyun verileri yasaklanmaya yol açar.",
            ["JP"] = "OpenElevenはKonamiとは無関係の、独立した非営利のファンサーバーです。" +
                     "他のプレイヤーに敬意を持って接してください。チート、迷惑行為、改変されたゲームデータの使用はBANの対象となります。",
            ["KR"] = "OpenEleven은 Konami와 관련 없는 독립적인 비상업적 팬 서버입니다. " +
                     "다른 플레이어를 존중해 주세요. 치트, 악용 및 수정된 게임 데이터 사용은 이용 정지 대상입니다.",
            ["RU"] = "OpenEleven — независимый некоммерческий фанатский сервер, не связанный с Konami. " +
                     "Уважайте других игроков. Читерство, злоупотребления и изменённые игровые данные ведут к бану.",
            ["HE"] = "OpenEleven הוא שרת מעריצים עצמאי ולא מסחרי, שאינו קשור ל-Konami. " +
                     "כבדו את השחקנים האחרים. רמאות, התנהגות פוגענית ונתוני משחק ששונו יובילו לחסימה.",
            ["CH"] = "OpenEleven 是一個獨立、非商業性的玩家伺服器，與 Konami 無關。" +
                     "請尊重其他玩家。作弊、濫用及修改遊戲資料將導致封鎖。",
        },
        ["dlc.description"] = new()
        {
            ["EN"] = "OpenEleven data update", ["FR"] = "Mise à jour des données OpenEleven", ["GR"] = "OpenEleven-Datenupdate",
            ["IT"] = "Aggiornamento dati OpenEleven", ["SP"] = "Actualización de datos de OpenEleven", ["PR"] = "Atualização de dados do OpenEleven",
            ["NL"] = "OpenEleven-data-update", ["SV"] = "OpenEleven datauppdatering", ["TR"] = "OpenEleven veri güncellemesi",
            ["JP"] = "OpenElevenデータ更新", ["KR"] = "OpenEleven 데이터 업데이트", ["RU"] = "Обновление данных OpenEleven",
            ["HE"] = "עדכון נתונים של OpenEleven", ["CH"] = "OpenEleven 資料更新",
        },
        ["room.quickmatch"] = new()
        {
            ["EN"] = "Quick match", ["FR"] = "Match rapide", ["GR"] = "Schnelles Spiel", ["IT"] = "Partita rapida",
            ["SP"] = "Partido rápido", ["PR"] = "Partida rápida", ["NL"] = "Snelle wedstrijd", ["SV"] = "Snabbmatch",
            ["TR"] = "Hızlı maç", ["JP"] = "クイックマッチ", ["KR"] = "빠른 경기", ["RU"] = "Быстрый матч",
            ["HE"] = "משחק מהיר", ["CH"] = "快速比賽",
        },
        ["room.default"] = new()
        {
            ["EN"] = "Room {0}", ["FR"] = "Salon de {0}", ["GR"] = "Raum von {0}", ["IT"] = "Stanza di {0}",
            ["SP"] = "Sala de {0}", ["PR"] = "Sala de {0}", ["NL"] = "Kamer van {0}", ["SV"] = "Rum för {0}",
            ["TR"] = "{0} odası", ["JP"] = "{0}のルーム", ["KR"] = "{0}의 방", ["RU"] = "Комната {0}",
            ["HE"] = "החדר של {0}", ["CH"] = "{0} 的房間",
        },
        ["friendreq.title"] = new()
        {
            ["EN"] = "Friend request from {0}", ["FR"] = "Demande d'ami de {0}", ["GR"] = "Freundschaftsanfrage von {0}",
            ["IT"] = "Richiesta di amicizia da {0}", ["SP"] = "Solicitud de amistad de {0}", ["PR"] = "Pedido de amizade de {0}",
            ["NL"] = "Vriendschapsverzoek van {0}", ["SV"] = "Vänförfrågan från {0}", ["TR"] = "{0} arkadaşlık isteği gönderdi",
            ["JP"] = "{0}からのフレンド申請", ["KR"] = "{0}님의 친구 요청", ["RU"] = "Запрос в друзья от {0}",
            ["HE"] = "בקשת חברות מ-{0}", ["CH"] = "來自 {0} 的好友邀請",
        },
        ["friendreq.body"] = new()
        {
            ["EN"] = "{0} wants to be your friend.", ["FR"] = "{0} veut devenir votre ami.", ["GR"] = "{0} möchte dein Freund sein.",
            ["IT"] = "{0} vuole essere tuo amico.", ["SP"] = "{0} quiere ser tu amigo.", ["PR"] = "{0} quer ser seu amigo.",
            ["NL"] = "{0} wil je vriend worden.", ["SV"] = "{0} vill bli din vän.", ["TR"] = "{0} arkadaşın olmak istiyor.",
            ["JP"] = "{0}があなたとフレンドになりたがっています。", ["KR"] = "{0}님이 친구가 되고 싶어 합니다.",
            ["RU"] = "{0} хочет добавить вас в друзья.", ["HE"] = "{0} רוצה להיות חבר שלך.", ["CH"] = "{0} 想成為你的好友。",
        },
        ["friendok.title"] = new()
        {
            ["EN"] = "Friend request accepted", ["FR"] = "Demande d'ami acceptée", ["GR"] = "Freundschaftsanfrage angenommen",
            ["IT"] = "Richiesta di amicizia accettata", ["SP"] = "Solicitud de amistad aceptada", ["PR"] = "Pedido de amizade aceito",
            ["NL"] = "Vriendschapsverzoek geaccepteerd", ["SV"] = "Vänförfrågan accepterad", ["TR"] = "Arkadaşlık isteği kabul edildi",
            ["JP"] = "フレンド申請が承認されました", ["KR"] = "친구 요청이 수락되었습니다", ["RU"] = "Запрос в друзья принят",
            ["HE"] = "בקשת החברות אושרה", ["CH"] = "好友邀請已接受",
        },
        ["friendok.body"] = new()
        {
            ["EN"] = "{0} is now your friend.", ["FR"] = "{0} est maintenant votre ami.", ["GR"] = "{0} ist jetzt dein Freund.",
            ["IT"] = "{0} ora è tuo amico.", ["SP"] = "{0} ahora es tu amigo.", ["PR"] = "{0} agora é seu amigo.",
            ["NL"] = "{0} is nu je vriend.", ["SV"] = "{0} är nu din vän.", ["TR"] = "{0} artık arkadaşın.",
            ["JP"] = "{0}とフレンドになりました。", ["KR"] = "{0}님과 친구가 되었습니다.", ["RU"] = "{0} теперь ваш друг.",
            ["HE"] = "{0} הוא עכשיו חבר שלך.", ["CH"] = "{0} 現在是你的好友。",
        },
        ["promoted.title"] = new()
        {
            ["EN"] = "Promoted to {0}", ["FR"] = "Promu en {0}", ["GR"] = "Aufgestiegen in {0}",
            ["IT"] = "Promosso in {0}", ["SP"] = "Ascendido a {0}", ["PR"] = "Promovido para {0}",
            ["NL"] = "Gepromoveerd naar {0}", ["SV"] = "Uppflyttad till {0}", ["TR"] = "{0} kümesine yükseldin",
            ["JP"] = "{0}に昇格", ["KR"] = "{0}(으)로 승격", ["RU"] = "Повышение в {0}",
            ["HE"] = "עלית ל-{0}", ["CH"] = "晉升至 {0}",
        },
        ["relegated.title"] = new()
        {
            ["EN"] = "Relegated to {0}", ["FR"] = "Relégué en {0}", ["GR"] = "Abgestiegen in {0}",
            ["IT"] = "Retrocesso in {0}", ["SP"] = "Descendido a {0}", ["PR"] = "Rebaixado para {0}",
            ["NL"] = "Gedegradeerd naar {0}", ["SV"] = "Nedflyttad till {0}", ["TR"] = "{0} kümesine düştün",
            ["JP"] = "{0}に降格", ["KR"] = "{0}(으)로 강등", ["RU"] = "Понижение в {0}",
            ["HE"] = "ירדת ל-{0}", ["CH"] = "降級至 {0}",
        },
        ["promoted.body"] = new()
        {
            ["EN"] = "Season {0} is over. You were promoted from {1} to {2}.",
            ["FR"] = "La saison {0} est terminée. Vous êtes promu de {1} en {2}.",
            ["GR"] = "Saison {0} ist vorbei. Du bist von {1} in {2} aufgestiegen.",
            ["IT"] = "La stagione {0} è finita. Sei stato promosso da {1} a {2}.",
            ["SP"] = "La temporada {0} terminó. Ascendiste de {1} a {2}.",
            ["PR"] = "A temporada {0} terminou. Você foi promovido de {1} para {2}.",
            ["NL"] = "Seizoen {0} is voorbij. Je bent gepromoveerd van {1} naar {2}.",
            ["SV"] = "Säsong {0} är slut. Du flyttades upp från {1} till {2}.",
            ["TR"] = "{0} sezonu sona erdi. {1} kümesinden {2} kümesine yükseldin.",
            ["JP"] = "シーズン{0}が終了しました。{1}から{2}に昇格しました。",
            ["KR"] = "시즌 {0}이(가) 끝났습니다. {1}에서 {2}(으)로 승격되었습니다.",
            ["RU"] = "Сезон {0} завершён. Вы перешли из {1} в {2}.",
            ["HE"] = "עונה {0} הסתיימה. עלית מ-{1} ל-{2}.",
            ["CH"] = "第 {0} 賽季已結束。你已從 {1} 晉升至 {2}。",
        },
        ["relegated.body"] = new()
        {
            ["EN"] = "Season {0} is over. You were relegated from {1} to {2}.",
            ["FR"] = "La saison {0} est terminée. Vous êtes relégué de {1} en {2}.",
            ["GR"] = "Saison {0} ist vorbei. Du bist von {1} in {2} abgestiegen.",
            ["IT"] = "La stagione {0} è finita. Sei retrocesso da {1} a {2}.",
            ["SP"] = "La temporada {0} terminó. Descendiste de {1} a {2}.",
            ["PR"] = "A temporada {0} terminou. Você foi rebaixado de {1} para {2}.",
            ["NL"] = "Seizoen {0} is voorbij. Je bent gedegradeerd van {1} naar {2}.",
            ["SV"] = "Säsong {0} är slut. Du flyttades ner från {1} till {2}.",
            ["TR"] = "{0} sezonu sona erdi. {1} kümesinden {2} kümesine düştün.",
            ["JP"] = "シーズン{0}が終了しました。{1}から{2}に降格しました。",
            ["KR"] = "시즌 {0}이(가) 끝났습니다. {1}에서 {2}(으)로 강등되었습니다.",
            ["RU"] = "Сезон {0} завершён. Вы опустились из {1} в {2}.",
            ["HE"] = "עונה {0} הסתיימה. ירדת מ-{1} ל-{2}.",
            ["CH"] = "第 {0} 賽季已結束。你已從 {1} 降級至 {2}。",
        },
    };
}
