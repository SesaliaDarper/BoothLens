namespace BoothLens.Core.Models;

/// <summary>商品页正文的一节，例如「◆同梱データ◆」+ 内容。</summary>
public sealed class DescSection
{
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
}

/// <summary>本地磁盘上一个候选资源（zip / unitypackage / 文件夹 / 图片）。</summary>
public sealed class LocalFile
{
    public string Path { get; set; } = "";
    public string? RootPath { get; set; }
    public string Name { get; set; } = "";

    /// <summary>zip | unitypackage | folder | image | other</summary>
    public string Kind { get; set; } = "other";

    public long Size { get; set; }
    public string? Mtime { get; set; }

    public long? ItemId { get; set; }
    public double? Confidence { get; set; }

    /// <summary>id_in_name | title_fuzzy | content | manual | null</summary>
    public string? MatchedBy { get; set; }

    /// <summary>auto | review | unknown | ignored</summary>
    public string Status { get; set; } = "unknown";

    public bool Imported { get; set; }
    public string? UpdatedAt { get; set; }

    // 以下为联表展示字段，不落库
    public string? Title { get; set; }
    public string? Thumb { get; set; }
    public string? ShopName { get; set; }
    public bool HasImage { get; set; }
}

public sealed class ImportJob
{
    public long Id { get; set; }
    /// <summary>Confirmed product identity at submission; null for a local-only import.</summary>
    public long? ItemId { get; set; }
    public string? LocalPath { get; set; }
    /// <summary>copy | unitypackage | uri</summary>
    public string Mode { get; set; } = "copy";
    /// <summary>queued | staged | done | failed | unsupported</summary>
    public string State { get; set; } = "queued";
    public string? Message { get; set; }
    public string? TargetPath { get; set; }
    public string? CreatedAt { get; set; }
    public string? FinishedAt { get; set; }

    // 展示字段
    public string? Title { get; set; }
}

public sealed class ScanResult
{
    public int Scanned { get; set; }
    public int New { get; set; }
    public int Auto { get; set; }
    public int Review { get; set; }
    public int Unknown { get; set; }
    public List<string> Errors { get; set; } = new();
}
