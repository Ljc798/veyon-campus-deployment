using System.Text.Json;

namespace VeyonCampus.Core;

public sealed record WebsitePolicyPresetLists(string BlockedDomains, string AllowedDomains,
    DateTimeOffset? UpdatedUtc);

/// <summary>Stores teacher-editable common website lists on the teacher computer.</summary>
public static class WebsitePolicyPresetStore
{
    private sealed record PresetFile(int SchemaVersion, string BlockedDomains, string AllowedDomains,
        DateTimeOffset UpdatedUtc);

    private const long MaximumFileBytes = 64 * 1024;
    private const int MaximumListCharacters = 24 * 1024;
    private static readonly object Gate = new();

private static readonly string[] DefaultBlockedDomains =
[
    // ==================== 1. 视频 / 影视 / 短视频 ====================
    "douyin.com", "kuaishou.com", "bilibili.com", "b23.tv",
    "ixigua.com", "haokan.com", "haokan.baidu.com",
    "v.qq.com", "iqiyi.com", "youku.com", "mgtv.com",
    "miguvideo.com", "tv.sohu.com", "pptv.com",
    "le.com", "letv.com", "acfun.cn", "acfun.com",
    "1905.com", "yangshipin.cn", "tv.cctv.com",
    "fun.tv", "pearvideo.com",
    "youtube.com", "youtu.be", "netflix.com", "disneyplus.com",
    "hulu.com", "primevideo.com", "vimeo.com",
    "dailymotion.com", "tiktok.com", "tubi.tv",
    "crunchyroll.com",

    // ==================== 2. 游戏直播 / 娱乐直播 ====================
    "douyu.com", "huya.com", "zhanqi.tv", "yy.com",
    "huajiao.com", "inke.cn", "cc.163.com",
    "twitch.tv", "kick.com",

    // ==================== 3. 小游戏 / 网页游戏 / 游戏平台 ====================
    "4399.com", "4399.cn", "7k7k.com", "3366.com",
    "2144.cn", "37.com", "37wan.com", "9377.com",
    "3839.com", "9game.cn", "17173.com", "52pk.com",
    "taptap.cn", "taptap.io", "3dmgame.com",
    "gamersky.com", "yxdown.com", "yikm.net",
    "game.qq.com", "wegame.com.cn", "wan.360.cn",
    "xiaoyouxi.2345.com",
    "poki.com", "crazygames.com", "y8.com",
    "miniclip.com", "friv.com", "agame.com",
    "gamepix.com", "kongregate.com", "addictinggames.com",
    "newgrounds.com", "armorgames.com", "1001games.com",
    "roblox.com", "store.steampowered.com",
    "steamcommunity.com", "store.epicgames.com",

    // ==================== 4. 社交 / 论坛 / 社区 ====================
    "xiaohongshu.com", "xhslink.com",
    "weibo.com", "weibo.cn", "douban.com",
    "tieba.baidu.com", "tianya.cn",
    "hupu.com", "nga.cn", "qzone.qq.com",
    "reddit.com", "facebook.com", "instagram.com",
    "threads.net", "x.com", "twitter.com",
    "snapchat.com", "pinterest.com", "tumblr.com",
    "t.me", "telegram.org", "discord.com", "vk.com",

    // ==================== 5. 地图 / 导航 ====================
    "amap.com", "gaode.com", "map.baidu.com",
    "map.qq.com", "map.sogou.com",
    "maps.google.com", "maps.google.cn",
    "maps.apple.com", "openstreetmap.org",
    "earth.google.com", "earth.google.cn",

    // ==================== 6. 购物 / 电商 ====================
    "taobao.com", "tmall.com", "jd.com",
    "1688.com", "pinduoduo.com", "yangkeduo.com",
    "vip.com", "suning.com", "dangdang.com",
    "kaola.com", "dewu.com", "gome.com.cn",
    "smzdm.com", "manmanbuy.com",
    "amazon.com", "amazon.cn", "ebay.com",
    "aliexpress.com", "temu.com", "shein.com",
    "shopee.com", "lazada.com", "walmart.com",
    "bestbuy.com", "target.com", "costco.com",
    "etsy.com", "rakuten.co.jp", "mercari.com",
    "mercari.jp", "stockx.com", "farfetch.com",
    "dhgate.com",

    // ==================== 7. 新闻 / 资讯 / 财经 ====================
    "news.qq.com", "news.163.com",
    "news.sina.com.cn", "news.sohu.com",
    "news.ifeng.com", "toutiao.com", "toutiao.cn",
    "thepaper.cn", "guancha.cn", "huanqiu.com",
    "chinanews.com.cn", "xinhuanet.com",
    "people.com.cn", "gmw.cn", "cnr.cn",
    "yicai.com", "jiemian.com", "caixin.com",
    "cls.cn", "eastmoney.com", "10jqka.com.cn",
    "xueqiu.com", "wallstreetcn.com",
    "36kr.com", "huxiu.com", "ithome.com",
    "pingwest.com",
    "cnn.com", "bbc.com", "nytimes.com",
    "theguardian.com", "reuters.com",
    "bloomberg.com", "foxnews.com",

    // ==================== 8. 音乐 / 电台 / 音频 ====================
    "music.163.com", "y.qq.com",
    "kugou.com", "kuwo.cn", "qishui.com",
    "music.migu.cn", "ximalaya.com",
    "qingting.fm", "lizhi.fm",
    "spotify.com", "music.apple.com",
    "soundcloud.com", "bandcamp.com",
    "deezer.com", "tidal.com", "pandora.com",
    "last.fm", "audiomack.com",
    "tunein.com", "iheart.com",

    // ==================== 9. 旅游 / 酒店 / 机票 ====================
    "ctrip.com", "trip.com", "qunar.com",
    "fliggy.com", "alitrip.com", "ly.com",
    "tuniu.com", "mafengwo.cn", "qyer.com",
    "elong.com", "booking.com", "agoda.com",
    "expedia.com", "hotels.com", "airbnb.com",
    "tripadvisor.com", "klook.com", "skyscanner.com",
    "kayak.com", "trivago.com", "hostelworld.com",
    "vrbo.com", "traveloka.com",
    "travel.google.com",

    // ==================== 10. 体育 / 赛事 ====================
    "sports.qq.com", "sports.sina.com.cn",
    "nba.com", "espn.com", "zhibo8.com",
    "dongqiudi.com", "sofascore.com",
    "flashscore.com", "livescore.com",

    // ==================== 11. 本地生活 / 外卖 / 娱乐票务 ====================
    "meituan.com", "dianping.com",
    "ele.me", "eleme.cn", "koubei.com",
    "damai.cn", "maoyan.com",
    "taopiaopiao.com", "piaoniu.com",


    // ==================== 12. 房产 / 二手房 / 租房 ====================
    "anjuke.com",         // 安居客
    "ke.com",             // 贝壳找房
    "lianjia.com",        // 链家
    "fang.com",           // 房天下
    "5i5j.com",           // 我爱我家
    "ziroom.com",         // 自如
    "fangdd.com",         // 房多多
    "centanet.com",       // 中原地产
    "xiangyu.com",        // 相寓
    "focus.cn",           // 搜狐焦点房产
    "leju.com",           // 乐居房产
    "jiwu.com",           // 吉屋网
    "loupan.com",         // 楼盘网
    "58.com",             // 58同城（包含房产和二手交易）
    "ganji.com",          // 赶集网

    // ==================== 13. 汽车 / 二手车 / 汽车资讯 ====================
    "autohome.com.cn",    // 汽车之家
    "che168.com",         // 二手车之家
    "dongchedi.com",      // 懂车帝
    "guazi.com",          // 瓜子二手车
    "yiche.com",          // 易车
    "bitauto.com",        // 易车相关域名
    "pcauto.com.cn",      // 太平洋汽车
    "xcar.com.cn",        // 爱卡汽车
    "xin.com",            // 优信二手车
    "renrenche.com",      // 人人车
    "taoche.com",         // 淘车
    "taocheche.com",      // 淘车车
    "taocheche.com.cn",   // 淘车车
    "ttpai.cn",           // 天天拍车
    "che300.com",         // 车300


    // ==================== 13. 娱乐门户 / 网站导航 ====================
        "hao123.com", "265.com",
        "hao.qq.com", "hao.360.cn",
        "123.sogou.com", "2345.com"
    ];

    private static readonly string[] DefaultAllowedDomains =
    [
        "smartedu.cn", "icourse163.org", "xuetangx.com", "bdfjiaoyu.com"
    ];
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    public static string DefaultDirectory => WebsitePolicyListDraftStore.DefaultDirectory;

    public static WebsitePolicyPresetLists Read(string? directory = null)
    {
        var path = GetPath(directory);
        lock (Gate)
        {
            if (!File.Exists(path)) return CreateDefaults();
            PathLinkSecurity.RejectLinks(path);
            var info = new FileInfo(path);
            if (info.Length is <= 0 or > MaximumFileBytes)
                throw new InvalidDataException("常用网站名单文件大小无效。");
            var bytes = File.ReadAllBytes(path);
            PolicyJson.RejectDuplicateFields(bytes);
            var file = JsonSerializer.Deserialize<PresetFile>(bytes, Options)
                       ?? throw new InvalidDataException("常用网站名单文件为空。");
            if (file.SchemaVersion != 1 || file.BlockedDomains is null || file.AllowedDomains is null ||
                file.BlockedDomains.Length > MaximumListCharacters ||
                file.AllowedDomains.Length > MaximumListCharacters)
                throw new InvalidDataException("常用网站名单文件版本或内容无效。");

            return new WebsitePolicyPresetLists(NormalizeList(file.BlockedDomains),
                NormalizeList(file.AllowedDomains), file.UpdatedUtc);
        }
    }

    public static WebsitePolicyPresetLists Save(string blockedDomains, string allowedDomains,
        string? directory = null)
    {
        ArgumentNullException.ThrowIfNull(blockedDomains);
        ArgumentNullException.ThrowIfNull(allowedDomains);
        if (blockedDomains.Length > MaximumListCharacters || allowedDomains.Length > MaximumListCharacters)
            throw new InvalidDataException("常用网站名单超过本机保存限制。");

        var blocked = NormalizeList(blockedDomains);
        var allowed = NormalizeList(allowedDomains);
        var saved = new WebsitePolicyPresetLists(blocked, allowed, DateTimeOffset.UtcNow);
        lock (Gate) WriteFile(GetPath(directory), saved);
        return saved;
    }

    private static WebsitePolicyPresetLists CreateDefaults() =>
        new(string.Join(Environment.NewLine, DefaultBlockedDomains),
            string.Join(Environment.NewLine, DefaultAllowedDomains), null);

    private static string NormalizeList(string value)
    {
        var entries = value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        return string.Join(Environment.NewLine, WebsitePolicyCompiler.NormalizeDomains(entries));
    }

    private static string GetPath(string? directory) => Path.Combine(
        Path.GetFullPath(directory ?? DefaultDirectory), "website-policy-presets.json");

    private static void WriteFile(string path, WebsitePolicyPresetLists presets)
    {
        var parent = Path.GetDirectoryName(path) ?? throw new InvalidDataException("常用网站名单路径无效。");
        Directory.CreateDirectory(parent);
        PathLinkSecurity.RejectLinks(parent);
        if (File.Exists(path)) PathLinkSecurity.RejectLinks(path);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new PresetFile(1, presets.BlockedDomains, presets.AllowedDomains, presets.UpdatedUtc!.Value), Options);
        if (bytes.Length > MaximumFileBytes)
            throw new InvalidDataException("常用网站名单文件超过大小限制。");

        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       8192, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
        }
    }
}
