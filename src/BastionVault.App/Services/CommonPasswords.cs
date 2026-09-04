using System.Collections.Frozen;

namespace BastionVault.App.Services;

/// <summary>
/// The embedded common-password dictionary used by <see cref="PasswordStrength"/>. The core is a
/// curated list of the passwords that dominate every public breach corpus, ordered roughly by
/// frequency; it is then extended with the mechanical variants those lists are full of (a word
/// with "1", "12", "123", "!" or a recent year appended), which is how real users "strengthen" a
/// weak password. Rank matters: the estimator charges log2(rank) bits for a hit, so
/// <c>123456</c> costs a bit and <c>trustno1</c> costs eleven.
/// </summary>
/// <remarks>
/// Provenance: the list was written for this project from general knowledge of the passwords
/// that top publicly analysed breach corpora (RockYou and its successors). No third-party list
/// was copied, and no entry carries any attribution requirement.
/// </remarks>
public static class CommonPasswords
{
    private const string Core = """
        123456 password 12345678 qwerty 123456789 12345 1234 111111 1234567 dragon
        123123 baseball abc123 football monkey letmein shadow master 666666 qwertyuiop
        123321 mustang 1234567890 michael 654321 superman 1qaz2wsx 7777777 121212 000000
        qazwsx 123qwe killer trustno1 jordan jennifer zxcvbnm asdfgh hunter buster
        soccer harley batman andrew tigger sunshine iloveyou 2000 charlie robert
        thomas hockey ranger daniel starwars klaster 112233 george computer michelle
        jessica pepper 1111 zxcvbn 555555 11111111 131313 freedom 777777 pass
        maggie 159753 aaaaaa ginger princess joshua cheese amanda summer love ashley
        nicole chelsea biteme matthew access yankees 987654321 dallas austin thunder
        taylor matrix mobilemail mom monitor monitoring montana moon moscow
        secret hello whatever nothing money merlin qwerty123 1q2w3e4r asdfghjkl
        1q2w3e 123abc passw0rd p@ssword p@ssw0rd welcome admin administrator root
        guest test test123 changeme default temp temp123 letmein123 iloveu
        blahblah sammy silver internet samsung google amazon spider bandit
        golfer angels heather hammer summer1 winter spring autumn january
        february march april may june july august september october november december
        London Paris newyork tokyo berlin madrid rome sydney chicago boston
        liverpool arsenal chelseafc barcelona realmadrid juventus milan bayern
        cowboys steelers packers yankees1 lakers celtics rangers1 giants eagles
        redsox patriots dolphins raiders broncos vikings saints falcons
        asdf asdfasdf qweasd qweasdzxc zaq12wsx xsw2zaq1 1qazxsw2 qazxsw
        poiuytrewq lkjhgfdsa mnbvcxz 0987654321 abcdef abcd1234 a1b2c3 a1b2c3d4
        password1 password123 password12 passwords pa55word passwd
        qwerty1 qwerty12 qwertyui asdf1234 zxcv1234 1234qwer
        michael1 jordan23 michael23 jordan1 tigger1 buster1 shadow1
        sophie oliver jacob emily olivia emma ava mia isabella
        william james john robert1 david richard joseph charles christopher
        anthony mark donald steven paul andrew1 joshua1 kenneth kevin brian
        sarah karen nancy lisa betty helen sandra donna carol ruth sharon
        laura kimberly deborah dorothy amy angela melissa brenda anna rebecca
        virginia kathleen pamela martha debra amber1 marilyn janet catherine
        arsenal1 chelsea1 liverpool1 united manutd mufc lfc cfc
        harrypotter starwars1 pokemon nintendo playstation xbox minecraft fortnite
        superman1 batman1 spiderman ironman avengers hulk thor loki
        matrix1 neo trinity morpheus gandalf frodo legolas aragorn
        cheese1 pizza burger coffee chocolate cookie banana apple orange
        purple yellow green1 blue1 black white silver1 golden red123
        flower rainbow butterfly diamond crystal angel1 devil demon
        hunter1 fisher farmer builder driver runner walker rider
        dolphin1 tiger lion panther jaguar cobra viper falcon1 eagle1
        mustang1 camaro corvette porsche ferrari mercedes toyota honda1 nissan
        bmw audi ford chevy dodge jeep subaru mazda
        freedom1 liberty justice victory1 destiny fortune miracle magic1
        secret1 private hidden mystery shadow123 phantom ghost spirit
        winter1 summer12 spring1 autumn1 december1 november1
        forever always never nothing1 everything anything something
        happy1 lucky1 sunny stormy cloudy rainy snowy windy
        123456a a123456 1234561 12345a abcdefg 1a2b3c 1a2b3c4d
        qwe123 asd123 zxc123 qaz123 wsx123 edc123 rfv123 tgb123
        letmein1 openup opensesame knockknock hellothere goodbye
        trustme believe hopeful faithful loyal honest brave1
        monkey1 monkey123 donkey1 rabbit hamster kitten puppy1
        firebird phoenix1 dragon1 dragon123 wizard warlock knight
        samurai ninja1 pirate viking spartan gladiator warrior1
        soccer1 basketball1 football1 baseball1 tennis1 golf1 hockey1
        swimming running cycling boxing wrestling fishing hunting1
        guitar1 piano drums violin trumpet music1 melody rhythm
        beatles metallica nirvana queen1 acdc pinkfloyd ledzeppelin
        eminem rihanna beyonce madonna1 elvis1 bowie prince1
        starwars123 startrek stargate babylon5 firefly serenity
        gameofthrones breakingbad thewire sopranos friends1 seinfeld
        windows linux1 macintosh android iphone1 samsung1 nokia1
        oracle1 mysql postgres mongodb redis1 docker kubernetes
        server1 client1 network router firewall gateway1 proxy1
        backup1 restore archive1 storage database1 filesystem
        january1 february1 march1 april1 june1 july1 august1
        monday tuesday wednesday thursday friday1 saturday sunday1
        morning evening midnight noon1 sunrise sunset twilight
        northstar southpaw eastside westcoast midwest downtown
        homeless homework housework schoolwork paperwork network1
        birthday1 christmas1 halloween thanksgiving easter1 newyear
        valentine holiday1 vacation weekend party1 wedding
        family1 mother father brother sister1 grandma grandpa
        babygirl babyboy sweetie honey1 darling sweetheart lover1
        kisses hugs love123 loveme lovely1 loved iloveyou1 iloveyou2
        forever1 together alone1 lonely happy123 smile1 laugh
        hello123 hello1 helloworld goodmorning goodnight howareyou
        whatsup nothing123 whatever1 anyway maybe1 perhaps
        yesyes nono okay1 fine1 great1 awesome cool1 nice1
        superstar champion winner1 loser1 player1 gamer1 hacker1
        cracker spammer phisher scammer1 stalker creeper
        password2 password3 password11 password99 password2020 password2021
        password2022 password2023 password2024 password2025
        admin123 admin1 admin12 administrator1 sysadmin webadmin
        support helpdesk service1 operator manager1 director
        student teacher1 professor doctor1 nurse1 lawyer engineer
        developer designer analyst consultant assistant intern
        company business office1 factory1 warehouse shipping
        finance accounting marketing1 sales1 support1 legal1
        secret123 topsecret classified confidential restricted
        letmein12 letmein2 letmethrough openthedoor unlockme
        killer1 slayer destroyer1 crusher smasher breaker
        123456789a 1234567891 12345678910 11223344 12341234
        112211 998877 776655 554433 332211 010203 020304
        abcabc xyzxyz aaabbb 111222 123123123 456456
        qwertyu qwertz azerty1 dvorak colemak
        iloveyou123 ilovegod ilovejesus jesus1 jesuschrist god123
        buddha allah1 heaven1 angel123 blessing prayer1 amen1
        naruto sasuke goku vegeta luffy zoro pikachu charizard
        sonic1 mario1 luigi zelda link1 samus kirby donkeykong
        halo117 masterchief cortana doom1 quake unreal counterstrike
        callofduty battlefield overwatch valorant leagueoflegends
        worldofwarcraft diablo starcraft warcraft3 hearthstone
        skyrim fallout witcher cyberpunk gta5 rdr2 assassinscreed
        """;

    private static readonly string[] Suffixes = ["1", "12", "123", "!", "2024", "2025", "01"];

    private static readonly FrozenDictionary<string, int> RankByPassword = Build();

    /// <summary>Number of entries in the dictionary.</summary>
    public static int Count => RankByPassword.Count;

    /// <summary>
    /// Returns the one-based rank of <paramref name="candidate"/>, or <see langword="null"/> when
    /// it is not a known common password. Comparison is ordinal and case-insensitive, and the
    /// span overload never materialises the candidate as a string (UI-CONTRACT.md section 1.3).
    /// </summary>
    /// <param name="candidate">A candidate token.</param>
    public static int? Rank(ReadOnlySpan<char> candidate)
    {
        FrozenDictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> lookup =
            RankByPassword.GetAlternateLookup<ReadOnlySpan<char>>();
        return lookup.TryGetValue(candidate, out int rank) ? rank : null;
    }

    /// <summary>Returns the one-based rank of <paramref name="candidate"/>, or <see langword="null"/>.</summary>
    /// <param name="candidate">A candidate token.</param>
    public static int? Rank(string candidate) =>
        RankByPassword.TryGetValue(candidate, out int rank) ? rank : null;

    private static FrozenDictionary<string, int> Build()
    {
        string[] core = Core.Split(
            [' ', '\r', '\n', '\t'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var ranks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int next = 1;

        foreach (string word in core)
        {
            string lower = word.ToLowerInvariant();
            if (ranks.TryAdd(lower, next))
            {
                next++;
            }
        }

        // The mechanical variants that fill out every real breach list. They rank behind the core,
        // which is exactly right: "monkey" is guessed before "monkey123".
        int coreCount = ranks.Count;
        string[] bases = [.. ranks.Keys.Take(Math.Min(coreCount, 400))];
        foreach (string suffix in Suffixes)
        {
            foreach (string word in bases)
            {
                if (word.Length < 4 || char.IsDigit(word[^1]))
                {
                    continue;
                }

                if (ranks.TryAdd(word + suffix, next))
                {
                    next++;
                }
            }
        }

        return ranks.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }
}
