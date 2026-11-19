using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using BoothLens.Core.Models;

namespace BoothLens.Core.Booth;

public sealed class ParsedDetail
{
    public string? Intro { get; set; }
    public List<DescSection> Sections { get; set; } = new();

    /// <summary>展平后的全文，供搜索与兜底匹配使用。</summary>
    public string Text
    {
        get
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(Intro)) sb.AppendLine(Intro);
            foreach (var s in Sections)
            {
                if (s.Title.Length > 0) sb.AppendLine(s.Title);
                if (s.Body.Length > 0) sb.AppendLine(s.Body);
            }
            return sb.ToString().Trim();
        }
    }
}

public sealed class SearchHit
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string? Brand { get; set; }
    public long? Price { get; set; }
    public string? Thumb { get; set; }
    public bool AlreadyLocal { get; set; }
    public string? ExistingTitle { get; set; }
}

public static class ItemParser
{
    private static readonly Regex UsersRx = new(@"/users/(\d+)/", RegexOptions.Compiled);

    // ---------- JSON ----------

    public static Item ParseItem(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;

        var item = new Item
        {
            Id = GetLong(r, "id") ?? 0,
            Title = GetString(r, "name") ?? "",
            Url = $"https://booth.pm/ja/items/{GetLong(r, "id")}",
            PriceText = GetString(r, "price"),
            PublishedAt = GetString(r, "published_at"),
            IsAdult = GetBool(r, "is_adult"),
            IsPlaceholder = GetBool(r, "is_placeholder"),
            IsEndOfSale = GetBool(r, "is_end_of_sale"),
            IsSoldOut = GetBool(r, "is_sold_out"),
            WishCount = GetLong(r, "wish_lists_count"),
            DescShort = GetString(r, "description"),
            FetchedAt = DateTimeOffset.Now.ToString("O"),
            FetchState = "ok"
        };

        if (item.Id == 0) throw new BoothException("商品 JSON 里没有 id 字段。");

        if (r.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String)
            item.CanonicalUrl = u.GetString();

        // shop：没有 shop.id，用 subdomain + uuid，数字 user id 从缩略图路径里提
        if (r.TryGetProperty("shop", out var shop) && shop.ValueKind == JsonValueKind.Object)
        {
            item.ShopName = GetString(shop, "name");
            item.ShopSubdomain = GetString(shop, "subdomain");
            item.ShopUuid = GetString(shop, "uuid");
            var icon = GetString(shop, "thumbnail_url");
            if (!string.IsNullOrEmpty(icon))
            {
                var m = UsersRx.Match(icon);
                if (m.Success && long.TryParse(m.Groups[1].Value, out var uid)) item.ShopUserId = uid;
            }
        }

        // 单图：images[0].original 就是大图（实测 1024x1024），不用改尺寸参数
        if (r.TryGetProperty("images", out var imgs) && imgs.ValueKind == JsonValueKind.Array && imgs.GetArrayLength() > 0)
        {
            var first = imgs[0];
            item.RemoteImage = GetString(first, "original") ?? GetString(first, "resized");
        }

        if (r.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
        {
            var names = new List<string>();
            foreach (var t in tags.EnumerateArray())
            {
                var n = GetString(t, "name");
                if (!string.IsNullOrWhiteSpace(n)) names.Add(n!);
            }
            if (names.Count > 0) item.TagsJson = JsonSerializer.Serialize(names);
        }

        if (r.TryGetProperty("category", out var cat) && cat.ValueKind == JsonValueKind.Object)
        {
            item.Category = GetString(cat, "name");
            if (cat.TryGetProperty("parent", out var par) && par.ValueKind == JsonValueKind.Object)
                item.CategoryParent = GetString(par, "name");
        }

        if (r.TryGetProperty("variations", out var vars) && vars.ValueKind == JsonValueKind.Array && vars.GetArrayLength() > 0)
            item.PriceValue = GetLong(vars[0], "price");

        return item;
    }

    // ---------- HTML：完整正文（分节） ----------

    public static ParsedDetail ParseDetail(string html)
    {
        var doc = new HtmlParser().ParseDocument(html);
        var detail = new ParsedDetail();

        var intro = doc.QuerySelector("div.js-market-item-detail-description");
        if (intro != null)
        {
            var t = Normalize(intro.TextContent);
            if (t.Length > 0) detail.Intro = t;
        }

        foreach (var sec in doc.QuerySelectorAll("section.shop__text"))
        {
            var h2 = sec.QuerySelector("h2");
            var p = sec.QuerySelector("p");
            var title = h2 == null ? "" : Normalize(h2.TextContent);
            var body = p == null ? Normalize(sec.TextContent) : Normalize(p.TextContent);
            if (title.Length == 0 && body.Length == 0) continue;
            detail.Sections.Add(new DescSection { Title = title, Body = body });
        }

        return detail;
    }

    private static string Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var t = s.Replace("\r\n", "\n").Replace('\u00a0', ' ');
        // 压缩 3 行以上空行
        t = Regex.Replace(t, @"\n{3,}", "\n\n");
        return t.Trim();
    }

    // ---------- HTML：搜索结果 ----------

    public static List<SearchHit> ParseSearch(string html)
    {
        var doc = new HtmlParser().ParseDocument(html);
        var hits = new List<SearchHit>();
        var seen = new HashSet<long>();

        foreach (var li in doc.QuerySelectorAll("li.item-card"))
        {
            var idRaw = li.GetAttribute("data-product-id");
            if (string.IsNullOrWhiteSpace(idRaw) || !long.TryParse(idRaw, out var id)) continue;
            if (!seen.Add(id)) continue;

            var hit = new SearchHit
            {
                Id = id,
                Name = li.GetAttribute("data-product-name") ?? "",
                Brand = li.GetAttribute("data-product-brand"),
                Thumb = li.QuerySelector("a.item-card__thumbnail-image")?.GetAttribute("data-original")
            };

            var priceRaw = li.GetAttribute("data-product-price");
            if (!string.IsNullOrWhiteSpace(priceRaw) && long.TryParse(priceRaw, out var pv)) hit.Price = pv;

            hits.Add(hit);
        }

        return hits;
    }

    // ---------- 小工具 ----------

    private static string? GetString(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (!e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.ToString(),
            _ => null
        };
    }

    private static long? GetLong(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (!e.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l)) return l;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var s)) return s;
        return null;
    }

    private static bool GetBool(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object) return false;
        if (!e.TryGetProperty(name, out var v)) return false;
        return v.ValueKind == JsonValueKind.True;
    }
}
