// Copyright (c) 2026 ESSVB. All Rights Reserved.

namespace EssvbSub.Core.Models;

/// <summary>
/// Curated game/media genre preset supplying tone guidance, terminology glossaries, and proper nouns.
/// </summary>
public sealed record GameGenrePreset(
    string Id,
    string DisplayName,
    string PromptGuidance,
    IReadOnlyDictionary<string, string> GlossaryTerms,
    IReadOnlyList<string> KeepNames)
{
    public override string ToString() => DisplayName;
}

public sealed record MatchedGlossaryContext(
    IReadOnlyList<(string Source, string Target)> Terms,
    IReadOnlyList<string> KeepNames);

public static class GameGenreCatalog
{
    private static IReadOnlyDictionary<string, string> Dict(params (string Key, string Value)[] pairs)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in pairs)
            d[k] = v;
        return d;
    }

    public static IReadOnlyList<GameGenrePreset> All { get; } =
    [
        new(
            "general",
            "Genel / Otomatik",
            "",
            Dict(
                ("Checkpoint", "Kayıt Noktası"),
                ("Save Point", "Kayıt Noktası"),
                ("Autosave", "Otomatik Kayıt"),
                ("Main Quest", "Ana Görev"),
                ("Side Quest", "Yan Görev"),
                ("Inventory", "Envanter"),
                ("Cutscene", "Ara Sahne"),
                ("Headshot", "Kafadan Vuruş")),
            []),

        new(
            "action_street",
            "Aksiyon / Açık Dünya / Suç (GTA, RDR2, Cyberpunk)",
            "Genre: Open-World Crime & Street Action. Keep street slang, swearing, banter, and tough-guy tone bold and authentic in natural spoken Turkish; never sanitize curses or soften threats.",
            Dict(
                ("Safehouse", "Güvenli Ev"),
                ("Wanted Level", "Aranma Seviyesi"),
                ("Heist", "Soygun"),
                ("Crew", "Ekip"),
                ("Score", "Büyük Vurgun"),
                ("Heat", "Polis Baskısı"),
                ("Cops", "Polisler"),
                ("Feds", "Federaller"),
                ("Fixer", "İş Bağlayıcı"),
                ("Bounty", "Ödül Avı"),
                ("Outlaw", "Kanun Kaçağı"),
                ("Sheriff", "Şerif"),
                ("Marshal", "Federal Şerif"),
                ("Stash", "Zula"),
                ("Netrunner", "Ağ Koşucusu"),
                ("Chrome", "Siber İmplant"),
                ("Ripperdoc", "Siber Doktor"),
                ("Turf", "Bölge"),
                ("Getaway", "Kaçış Aracı"),
                ("Snitch", "İspiyoncu"),
                ("Backup", "Destek Kuvvet"),
                ("Posse", "Çete")),
            ["Los Santos", "Vice City", "Leonida", "Jason", "Lucia", "Arthur", "Dutch", "V", "Night City"]),

        new(
            "rpg_fantasy",
            "RPG & Fantastik (God of War, Witcher, Elden Ring)",
            "Genre: Mythic & Fantasy RPG. Use heroic, atmospheric, slightly elevated Turkish for gods/knights while keeping companion banter natural. Respect fantasy lore titles consistently.",
            Dict(
                ("Quest", "Görev"),
                ("Side Quest", "Yan Görev"),
                ("Skill Tree", "Yetenek Ağacı"),
                ("Stamina", "Dayanıklılık"),
                ("Mana", "Mana"),
                ("Health", "Can"),
                ("Realm", "Diyar"),
                ("Rune", "Rün"),
                ("Relic", "Yadigâr"),
                ("Shrine", "Tapınak"),
                ("Lore", "Efsane"),
                ("Boss", "Bölüm Canavarı"),
                ("Tarnished", "Lekelenmiş"),
                ("Site of Grace", "Lütuf Noktası"),
                ("Witcher", "Efsunger"),
                ("Contract", "Kontrat"),
                ("Potion", "İksir"),
                ("Craft", "Üret"),
                ("Enchantment", "Büyü"),
                ("Valkyrie", "Valkür"),
                ("Draugr", "Draugr"),
                ("Spartan Rage", "Sparta Öfkesi")),
            ["Kratos", "Atreus", "Mimir", "Freya", "Odin", "Thor", "Geralt", "Ciri", "Yennefer", "Yggdrasil", "Midgard", "Leviathan"]),

        new(
            "fps_tactical",
            "FPS & Askeri Taktik (CoD, Battlefield, Tarkov)",
            "Genre: Military & Tactical Shooter. Keep radio callouts, breacher commands, and combat chatter short, punchy, and authentic to Turkish military/tactical jargon.",
            Dict(
                ("Copy", "Anlaşıldı"),
                ("Copy that", "Anlaşıldı, tamam"),
                ("Roger", "Tamam"),
                ("Oscar Mike", "Harekete geçildi"),
                ("Tangos", "Düşman hedefler"),
                ("Hostiles", "Düşman unsurlar"),
                ("Bogey", "Tanımsız hedef"),
                ("Reloading", "Şarjör değiştiriyorum"),
                ("Suppressing fire", "Baskı ateşi"),
                ("Flashbang", "Flaş bombası"),
                ("Frag out", "El bombası!"),
                ("Extraction", "Tahliye"),
                ("Evac", "Acil tahliye"),
                ("Loadout", "Teçhizat"),
                ("UAV", "İHA"),
                ("Airstrike", "Hava saldırısı"),
                ("Breach", "Kapıyı patlat / Gir"),
                ("Check fire", "Ateşi kes"),
                ("Friendly", "Dost unsur"),
                ("Sniper", "Keskin nişancı"),
                ("Hold position", "Pozisyonu koru")),
            ["Price", "Soap", "Ghost", "Gaz"]),

        new(
            "anime_jrpg",
            "Anime & JRPG (Persona, Final Fantasy, Yakuza)",
            "Genre: Anime & JRPG. Preserve Japanese honorific nuances naturally via Turkish tone (sen/siz, ağabey/abla for aniki/ane-san where fitting) without leaving romaji untranslated; match dramatic anime energy.",
            Dict(
                ("Sempaı", "Üst sınıf"),
                ("Senpai", "Üst sınıf"),
                ("Sensei", "Usta / Hocam"),
                ("Nakama", "Yoldaşlar"),
                ("Guild", "Lonca"),
                ("Party", "Grup"),
                ("Summon", "Çağır"),
                ("Limit Break", "Sınır Aşımı"),
                ("Turn", "Sıra"),
                ("Weakness", "Zayıflık"),
                ("Critical", "Kritik Vuruş"),
                ("Phoenix Down", "Anka Tüyü"),
                ("Save Point", "Kayıt Noktası"),
                ("Social Link", "Sosyal Bağ"),
                ("Confidant", "Sırdaş"),
                ("Shadow", "Gölge"),
                ("Dojo", "Dojo")),
            ["Kiryu", "Majima", "Ichiban", "Cloud", "Sephiroth", "Tifa", "Aerith", "Joker"]),

        new(
            "horror_survival",
            "Korku & Hayatta Kalma (Resident Evil, Last of Us)",
            "Genre: Survival Horror. Keep whispered tension, panic, medical/scavenging terms, and stark short warnings gripping and concise.",
            Dict(
                ("Infected", "Enfekteli"),
                ("Clickers", "Takırdayanlar"),
                ("First Aid", "İlk Yardım"),
                ("Medkit", "Sağlık Çantası"),
                ("Bandage", "Bandaj"),
                ("Flashlight", "El Feneri"),
                ("Battery", "Pil"),
                ("Lockpick", "Maymuncuk"),
                ("Keycard", "Giriş Kartı"),
                ("Safe Room", "Güvenli Oda"),
                ("Supply", "Erzak"),
                ("Ammo", "Mermi"),
                ("Outbreak", "Salgın"),
                ("Quarantine Zone", "Karantina Bölgesi"),
                ("Cure", "Tedavi")),
            ["Leon", "Claire", "Jill", "Chris", "Ada", "Wesker", "Joel", "Ellie"]),

        new(
            "strategy_scifi",
            "Strateji & Bilim Kurgu (Mass Effect, Stellaris, Halo)",
            "Genre: Sci-Fi & Grand Strategy. Translate technological, orbital, fleet, and diplomatic terms cleanly into natural Turkish sci-fi terminology.",
            Dict(
                ("Shields", "Kalkanlar"),
                ("Hull", "Gövde"),
                ("Warp", "Büküm Sıçraması"),
                ("Hyperdrive", "Hiper Sürücü"),
                ("Jump Gate", "Atlama Kapısı"),
                ("Relay", "Aktarıcı"),
                ("Fleet", "Filo"),
                ("Orbital", "Yörünge"),
                ("Beacon", "İşaretçi"),
                ("AI Core", "Yapay Zeka Çekirdeği"),
                ("Colony", "Koloni"),
                ("Outpost", "İleri Karakol"),
                ("Tech Tree", "Teknoloji Ağacı"),
                ("Diplomacy", "Diplomasi"),
                ("Truce", "Ateşkes"),
                ("Commander", "Komutan")),
            ["Shepard", "Garrus", "Liara", "Normandy", "Citadel", "Cortana", "Master Chief"]),

        new(
            "film_media",
            "Film, Dizi & YouTube / Belgesel",
            "Genre: Cinema, TV Series & Online Video. Prioritize smooth idiomatic Turkish dialogue, accurate jokes/sarcasm, cultural clarity, and readable 1-2 line subtitle rhythm.",
            Dict(
                ("Podcast", "Podcast"),
                ("Livestream", "Canlı Yayın"),
                ("Subscribe", "Abone Ol"),
                ("Sponsor", "Sponsor"),
                ("Spoiler", "Spoiler"),
                ("Season Finale", "Sezon Finali"),
                ("Plot Twist", "Ters Köşe"),
                ("Previously on", "Önceki bölümde")),
            [])
    ];

    public static GameGenrePreset Get(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return All[0];

        return All.FirstOrDefault(x => string.Equals(x.Id, id.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? All[0];
    }

    public static MatchedGlossaryContext MatchTermsForLine(
        GameGenrePreset preset,
        string sourceLine,
        IReadOnlyDictionary<string, string>? userTerms = null,
        IReadOnlyList<string>? userKeep = null)
    {
        if (string.IsNullOrWhiteSpace(sourceLine))
            return new([], []);

        var matchedTerms = new List<(string Source, string Target)>();
        var seenSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void ScanTerms(IReadOnlyDictionary<string, string>? dict)
        {
            if (dict is null || dict.Count == 0)
                return;

            foreach (var kv in dict.OrderByDescending(k => k.Key.Length))
            {
                if (matchedTerms.Count >= 12)
                    break;
                if (string.IsNullOrWhiteSpace(kv.Key) || string.IsNullOrWhiteSpace(kv.Value))
                    continue;
                if (seenSources.Contains(kv.Key))
                    continue;
                if (ContainsWholePhrase(sourceLine, kv.Key))
                {
                    seenSources.Add(kv.Key);
                    matchedTerms.Add((kv.Key, kv.Value));
                }
            }
        }

        // User-defined custom terms override built-in genre terms
        ScanTerms(userTerms);
        ScanTerms(preset.GlossaryTerms);

        var matchedKeep = new List<string>();
        var seenKeep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void ScanKeep(IReadOnlyList<string>? list)
        {
            if (list is null || list.Count == 0)
                return;

            foreach (var name in list)
            {
                if (matchedKeep.Count >= 8)
                    break;
                if (string.IsNullOrWhiteSpace(name) || seenKeep.Contains(name))
                    continue;
                if (ContainsWholePhrase(sourceLine, name))
                {
                    seenKeep.Add(name);
                    matchedKeep.Add(name);
                }
            }
        }

        ScanKeep(userKeep);
        ScanKeep(preset.KeepNames);

        return new MatchedGlossaryContext(matchedTerms, matchedKeep);
    }

    private static bool ContainsWholePhrase(string text, string phrase)
    {
        int start = 0;
        while (start < text.Length)
        {
            int idx = text.IndexOf(phrase, start, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                return false;

            bool leftOk = idx == 0 || !char.IsLetterOrDigit(text[idx - 1]);
            int end = idx + phrase.Length;
            bool rightOk = end >= text.Length || !char.IsLetterOrDigit(text[end]);
            if (leftOk && rightOk)
                return true;

            start = idx + 1;
        }
        return false;
    }
}
