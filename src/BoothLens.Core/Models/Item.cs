using System.Text.Json;
using System.Text.Json.Serialization;

namespace BoothLens.Core.Models;

/// <summary>一条 BOOTH 商品记录。一个商品在本地只对应一张图（Thumb）。</summary>
public sealed class Item
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public string TitleNorm { get; set; } = "";

    public string? ShopSubdomain { get; set; }
    public string? ShopUuid { get; set; }
    public long? ShopUserId { get; set; }
    public string? ShopName { get; set; }

    public string? Url { get; set; }
    public string? CanonicalUrl { get; set; }

    /// <summary>本地相对文件名，例如 "8962830.jpg"；null 表示还没抓到图。</summary>
    public string? Thumb { get; set; }

    public bool IsAdult { get; set; }
    public bool IsPlaceholder { get; set; }
    public bool IsEndOfSale { get; set; }
    public bool IsSoldOut { get; set; }

    public string? PriceText { get; set; }
    public long? PriceValue { get; set; }

    public string? Category { get; set; }
    public string? CategoryParent { get; set; }

    public string? TagsJson { get; set; }
    public long? WishCount { get; set; }
    public string? PublishedAt { get; set; }

    public string? DescShort { get; set; }
    public string? DescFull { get; set; }

    /// <summary>商品页正文的分节 JSON（[{"title":"◆同梱データ◆","body":"..."}]）。</summary>
    public string? SectionsJson { get; set; }

    public string? DescFetchedAt { get; set; }

    public string? FetchedAt { get; set; }
    public string FetchState { get; set; } = "pending";
    public string? FetchError { get; set; }
    public string CreatedAt { get; set; } = "";

    /// <summary>解析阶段临时携带的远端图片地址，不落库。</summary>
    [JsonIgnore]
    public string? RemoteImage { get; set; }

    [JsonIgnore]
    public bool HasImage => !string.IsNullOrEmpty(Thumb);

    /// <summary>供前端直接消费的分节正文。</summary>
    public IReadOnlyList<DescSection> Sections
    {
        get
        {
            if (string.IsNullOrWhiteSpace(SectionsJson)) return Array.Empty<DescSection>();
            try
            {
                return JsonSerializer.Deserialize<List<DescSection>>(SectionsJson)
                       ?? (IReadOnlyList<DescSection>)Array.Empty<DescSection>();
            }
            catch
            {
                return Array.Empty<DescSection>();
            }
        }
    }

    /// <summary>供前端直接消费的标签数组（只读计算属性，会被序列化出去）。</summary>
    public IReadOnlyList<string> Tags
    {
        get
        {
            if (string.IsNullOrWhiteSpace(TagsJson)) return Array.Empty<string>();
            try
            {
                return JsonSerializer.Deserialize<List<string>>(TagsJson) ?? (IReadOnlyList<string>)Array.Empty<string>();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }
    }
}
