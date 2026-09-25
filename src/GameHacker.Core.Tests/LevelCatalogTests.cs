using GameHacker.Core.Admin;
using GameHacker.Core.Levels;

namespace GameHacker.Core.Tests;

/// <summary>
/// 关卡文件的解析与校验。
/// </summary>
/// <remarks>
/// 这些错误如果漏到运行时，表现都是「进了关才卡住」—— 比如 check 里的机器名拼错，
/// 那一步永远判定不了完成。所以宁可在加载时就拒绝。
/// </remarks>
public class LevelCatalogTests
{
    private const string Lab = """
        {
          "id": "lab", "title": "实验场", "track": "tutorial",
          "networks": [ { "name": "lan", "vlan": 1, "subnet": "10.0.0.0/24" } ],
          "machines": [
            { "name": "a", "nics": [ { "network": "lan", "ip": "10.0.0.1" } ] },
            { "name": "b", "nics": [ { "network": "lan", "ip": "10.0.0.2" } ], "persona": "legacy-server" }
          ],
          "steps": [ { "id": "s", "title": "通", "check": { "type": "ping", "from": "b", "to": "a" } } ]
        }
        """;

    private static LevelCatalog Parse(params string[] jsons) =>
        LevelCatalog.Parse(jsons.Select((j, i) => ($"level{i}.json", j)));

    private static LevelFormatException Rejects(params string[] jsons) =>
        Assert.Throws<LevelFormatException>(() => Parse(jsons));

    [Fact]
    public void 解析出机器步骤与检测条件()
    {
        var level = Assert.Single(Parse(Lab).Levels);
        Assert.Equal(LevelTrack.Tutorial, level.Track);
        Assert.Equal(LevelStatus.Playable, level.Status);
        Assert.Equal(["a", "b"], level.Machines.Select(m => m.Name));
        Assert.Equal("workstation", level.Machines[0].Persona);
        var check = Assert.IsType<PingCheck>(level.Steps[0].Check);
        Assert.Equal(("b", "a"), (check.From, check.To));
    }

    [Fact]
    public void 教学关排在实战关前面_同一条线里按_order()
    {
        var catalog = Parse(
            """{ "id": "m2", "title": "x", "track": "mission", "order": 2, "status": "draft" }""",
            """{ "id": "m1", "title": "x", "track": "mission", "order": 1, "status": "draft" }""",
            """{ "id": "t9", "title": "x", "track": "tutorial", "order": 9, "status": "draft" }""");
        Assert.Equal(["t9", "m1", "m2"], catalog.Levels.Select(l => l.Id));
    }

    [Fact]
    public void 拼错的字段名被拒绝而不是忽略()
    {
        // "require" 少了个 s：忽略的话这关会悄悄变成不需要前置
        var ex = Rejects("""{ "id": "x", "title": "x", "track": "mission", "status": "draft", "require": ["lab"] }""");
        Assert.Contains("require", ex.Message);
    }

    [Fact]
    public void 未知的检测类型被拒绝()
    {
        var ex = Rejects(Lab.Replace("\"ping\"", "\"portscan\""));
        Assert.Contains("level0.json", ex.Message);
    }

    [Fact]
    public void 检测条件引用不存在的机器被拒绝()
    {
        var ex = Rejects(Lab.Replace("\"from\": \"b\"", "\"from\": \"c\""));
        Assert.Contains("\"c\" 不存在", ex.Message);
    }

    [Fact]
    public void 可玩关的步骤必须有检测_草稿关可以没有()
    {
        string noCheck = """
            { "id": "x", "title": "x", "track": "mission",
              "networks": [ { "name": "lan", "vlan": 1, "subnet": "10.0.0.0/24" } ],
              "machines": [ { "name": "a", "nics": [ { "network": "lan", "ip": "10.0.0.1" } ] } ],
              "steps": [ { "id": "s", "title": "待定" } ] }
            """;
        Assert.Contains("没有 check", Rejects(noCheck).Message);
        Parse(noCheck.Replace("\"track\": \"mission\"", "\"track\": \"mission\", \"status\": \"draft\""));
    }

    [Fact]
    public void 所有错误攒齐一起报()
    {
        string bad = """
            { "id": "Bad_Id", "title": "x", "track": "mission",
              "networks": [ { "name": "lan", "vlan": 1, "subnet": "10.0.0.0/24" } ],
              "machines": [ { "name": "a", "nics": [ { "network": "lan", "ip": "10.0.0.300" } ], "persona": "mainframe" },
                            { "name": "a", "nics": [ { "network": "lan", "ip": "10.0.0.2" } ] } ],
              "steps": [ { "id": "s", "title": "x", "check": { "type": "ping", "from": "a", "to": "a" } } ] }
            """;
        var ex = Rejects(bad);
        Assert.Contains(ex.Errors, e => e.Contains("Bad_Id"));
        Assert.Contains(ex.Errors, e => e.Contains("10.0.0.300"));
        Assert.Contains(ex.Errors, e => e.Contains("mainframe"));
        Assert.Contains(ex.Errors, e => e.Contains("机器名 \"a\" 重复"));
    }

    [Fact]
    public void 重复_id_与不存在的前置关被拒绝()
    {
        string draft(string id, string requires) =>
            $$"""{ "id": "{{id}}", "title": "x", "track": "mission", "status": "draft", "requires": [{{requires}}] }""";
        Assert.Contains("重复", Rejects(draft("a", ""), draft("a", "")).Message);
        Assert.Contains("\"ghost\" 不存在", Rejects(draft("a", "\"ghost\"")).Message);
    }

    [Fact]
    public void 解锁条件成环被拒绝()
    {
        string draft(string id, string req) =>
            $$"""{ "id": "{{id}}", "title": "x", "track": "mission", "status": "draft", "requires": ["{{req}}"] }""";
        var ex = Rejects(draft("a", "b"), draft("b", "c"), draft("c", "a"));
        Assert.Contains("成环", ex.Message);
    }

    [Fact]
    public void 仓库里的关卡文件都能通过校验()
    {
        // 游戏发布的就是这些文件，放进单元测试里，改坏了 CI 上就能看到
        string dir = Path.Combine(TestImages.RepoRoot, "src", "GameHacker.Godot", "levels");
        var files = Directory.GetFiles(dir, "*.json").Order(StringComparer.Ordinal).ToList();
        Assert.NotEmpty(files);

        var catalog = LevelCatalog.Parse(files.Select(f => (Path.GetFileName(f), File.ReadAllText(f))));
        // 文件名和 id 一致，找关卡文件时不用挨个打开
        Assert.Equal(files.Select(Path.GetFileNameWithoutExtension).Order(StringComparer.Ordinal),
                     catalog.Levels.Select(l => l.Id).Order(StringComparer.Ordinal));
        // 新存档至少有一关能进
        Assert.Contains(catalog.Levels, l => l.Status == LevelStatus.Playable && l.Requires.Count == 0);
    }
}

/// <summary>网段与网卡的校验。跳板关的「够不着内网」全靠这套配置不出错。</summary>
public class LevelNetworkTests
{
    private static string Level(string networks, string machines) => $$"""
        { "id": "n", "title": "n", "track": "mission", "status": "draft",
          "networks": [ {{networks}} ],
          "machines": [ {{machines}} ] }
        """;

    private const string TwoNets = """
        { "name": "outside", "vlan": 10, "subnet": "10.0.0.0/24" },
        { "name": "inside",  "vlan": 20, "subnet": "172.16.5.0/24" }
        """;

    private static LevelFormatException Rejects(string json) =>
        Assert.Throws<LevelFormatException>(() => LevelCatalog.Parse([("n.json", json)]));

    [Fact]
    public void 跳板机两块网卡分别进两个_VLAN()
    {
        var level = LevelCatalog.Parse([("n.json", Level(TwoNets, """
            { "name": "ws",     "nics": [ { "network": "outside", "ip": "10.0.0.1" } ] },
            { "name": "jump01", "nics": [ { "network": "outside", "ip": "10.0.0.2" },
                                          { "network": "inside",  "ip": "172.16.5.1" } ] }
            """))]).Levels[0];

        Assert.Equal([10, 20], LevelTopology.Vlans(level));
        var nics = LevelTopology.Nics(level, 1);
        Assert.Equal([10, 20], nics.Select(n => n.Vlan));
        Assert.Equal(["10.0.0.2/24", "172.16.5.1/24"], nics.Select(n => n.IpWithPrefix));
        Assert.Equal(["52:54:00:00:02:00", "52:54:00:00:02:01"], nics.Select(n => n.Mac));
        Assert.Equal("52:54:00:00:01:00", LevelTopology.Nics(level, 0)[0].Mac);
    }

    [Fact]
    public void 地址不在所接网段里被拒绝()
    {
        // 最容易犯的错：内网机器抄了外网的地址。客户机照样能起来，只是永远 ping 不通
        var ex = Rejects(Level(TwoNets, """{ "name": "f", "nics": [ { "network": "inside", "ip": "10.0.0.20" } ] }"""));
        Assert.Contains("不在网段 inside", ex.Message);
    }

    [Fact]
    public void 网段本身的各种错误被拒绝()
    {
        var ex = Rejects(Level("""
            { "name": "a", "vlan": 10, "subnet": "10.0.0.1/24" },
            { "name": "b", "vlan": 10, "subnet": "10.0.1.0/24" },
            { "name": "c", "vlan": 5000, "subnet": "10.0.2.0/24" }
            """, """{ "name": "m", "nics": [ { "network": "b", "ip": "10.0.1.1" } ] }"""));
        Assert.Contains(ex.Errors, e => e.Contains("10.0.0.1/24"));     // 主机位不为零
        Assert.Contains(ex.Errors, e => e.Contains("VLAN 10 重复"));
        Assert.Contains(ex.Errors, e => e.Contains("5000"));
    }

    [Fact]
    public void 网卡配置错误被拒绝()
    {
        var ex = Rejects(Level(TwoNets, """
            { "name": "a", "nics": [ { "network": "dmz", "ip": "10.0.0.1" } ] },
            { "name": "b", "nics": [ { "network": "outside", "ip": "10.0.0.2" },
                                     { "network": "outside", "ip": "10.0.0.3" } ] },
            { "name": "c", "nics": [ { "network": "inside", "ip": "172.16.5.255" } ] },
            { "name": "d", "nics": [] }
            """));
        Assert.Contains(ex.Errors, e => e.Contains("\"dmz\" 不存在"));
        Assert.Contains(ex.Errors, e => e.Contains("两块网卡接在同一个网段"));
        Assert.Contains(ex.Errors, e => e.Contains("广播地址"));
        Assert.Contains(ex.Errors, e => e.Contains("0 块网卡"));
    }

    [Fact]
    public void 白送终端和维护口不能同时给()
    {
        // 玩家坐在这台机器前面，再让他登录一次是白跑一趟
        var ex = Rejects(Level(TwoNets, """
            { "name": "ws",     "nics": [ { "network": "outside", "ip": "10.0.0.1" } ] },
            { "name": "jump01", "nics": [ { "network": "outside", "ip": "10.0.0.2" } ],
              "shell": true, "access": { "port": 2222, "user": "svc" } }
            """));
        Assert.Contains(ex.Errors, e => e.Contains("二选一"));
    }

    [Fact]
    public void 玩家自己的机器不该开维护口()
    {
        var ex = Rejects(Level(TwoNets, """
            { "name": "ws", "nics": [ { "network": "outside", "ip": "10.0.0.1" } ],
              "access": { "port": 2222, "user": "svc" } }
            """));
        Assert.Contains(ex.Errors, e => e.Contains("二选一"));
    }

    [Fact]
    public void 机器自带的维护口和服务自动进管理员白名单()
    {
        // 关卡作者不该手抄一遍：抄漏了，管理员就为机器自带的东西把玩家抓了；
        // 端口一改，手抄的那份还会悄悄失效
        var level = LevelCatalog.Parse([("n.json", """
            { "id": "n", "title": "n", "track": "mission", "status": "draft",
              "networks": [ { "name": "outside", "vlan": 10, "subnet": "10.0.0.0/24" } ],
              "machines": [
                { "name": "ws",     "nics": [ { "network": "outside", "ip": "10.0.0.1" } ] },
                { "name": "jump01", "nics": [ { "network": "outside", "ip": "10.0.0.2" } ],
                  "access": { "port": 2222, "user": "svc-backup" },
                  "files": [ { "path": "/srv/x.txt", "text": "x" } ],
                  "services": [ { "port": 9000, "file": "/srv/x.txt" } ] }
              ],
              "admin": { "machine": "jump01", "routine": [ { "id": "p", "check": "processes" } ] } }
            """)]).Levels[0];

        var allow = level.Admin!.Allow;
        Assert.Contains("nc -lk -p 2222 *", allow.Processes);
        Assert.Contains("nc -lk -p 9000 *", allow.Processes);
        Assert.Contains("script -q -E never *", allow.Processes);  // 服务端开 pty 的 script（root，无终端）
        Assert.Contains("svc-backup", allow.SessionUsers);         // 从维护口登录进来的会话本身不算痕迹
        Assert.Contains("tcp/2222", allow.Ports);
        Assert.Contains("tcp/9000", allow.Ports);

        // 真拿这份白名单去看进程：维护口的监听、服务端的 script 都得放行
        var whitelist = new ProcessWhitelist(allow.Processes);
        Assert.True(whitelist.Allows(new ProcessLine(1, "root", "?", "nc -lk -p 2222 -e /usr/sbin/rlogind")));
        Assert.True(whitelist.Allows(new ProcessLine(2, "root", "?", "script -q -E never -c stty... /dev/null")));
    }
}

/// <summary>「神器」blackwall 的开关：关卡逐关点名放出来，机器上再写能不能被接。</summary>
public class BlackwallSwitchTests
{
    private static string Level(string mode, bool target) => $$"""
        { "id": "b", "title": "b", "track": "mission", "status": "draft"{{(mode == "" ? "" : $", \"blackwall\": \"{mode}\"")}},
          "networks": [ { "name": "lan", "vlan": 1, "subnet": "10.0.0.0/24" } ],
          "machines": [
            { "name": "ws",  "nics": [ { "network": "lan", "ip": "10.0.0.1" } ] },
            { "name": "srv", "nics": [ { "network": "lan", "ip": "10.0.0.2" } ]{{(target ? ", \"blackwall\": true" : "")}} } ] }
        """;

    private static LevelDefinition Parse(string json) => LevelCatalog.Parse([("b.json", json)]).Levels[0];

    [Fact]
    public void 默认没有神器()
    {
        var level = Parse(Level("", target: false));
        Assert.Equal(BlackwallMode.Off, level.Blackwall);
        Assert.All(Enum.GetValues<PlayMode>(), mode => Assert.False(level.BlackwallEnabled(mode)));
    }

    [Fact]
    public void 新手关只给新手模式()
    {
        var level = Parse(Level("novice", target: true));
        Assert.True(level.BlackwallEnabled(PlayMode.Novice));
        Assert.False(level.BlackwallEnabled(PlayMode.Advanced));
        Assert.False(level.BlackwallEnabled(PlayMode.Expert));
    }

    [Fact]
    public void 剧情关任何模式都给_实战关也能点名()
    {
        var level = Parse(Level("story", target: true));
        Assert.All(Enum.GetValues<PlayMode>(), mode => Assert.True(level.BlackwallEnabled(mode)));
    }

    [Fact]
    public void 只在机器上写了而关卡没放出来就拦下()
    {
        var ex = Assert.Throws<LevelFormatException>(() => Parse(Level("", target: true)));
        Assert.Contains("没放出神器", ex.Message);
    }

    [Fact]
    public void 关卡放出来却没有可接的机器就拦下()
    {
        var ex = Assert.Throws<LevelFormatException>(() => Parse(Level("novice", target: false)));
        Assert.Contains("没有一台机器", ex.Message);
    }
}
