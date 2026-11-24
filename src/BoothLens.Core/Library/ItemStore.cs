using BoothLens.Core.Data;
using BoothLens.Core.Models;
using BoothLens.Core.Util;
using Microsoft.Data.Sqlite;

namespace BoothLens.Core.Library;

public sealed class ItemStore
{
    private readonly Db _db;

    public ItemStore(Db db) => _db = db;

    // ---------- 读取 ----------

    private static Item ReadItem(SqliteDataReader r)
    {
        string? S(string n) { var i = r.GetOrdinal(n); return r.IsDBNull(i) ? null : r.GetString(i); }
        long? L(string n) { var i = r.GetOrdinal(n); return r.IsDBNull(i) ? null : r.GetInt64(i); }
        bool B(string n) { var i = r.GetOrdinal(n); return !r.IsDBNull(i) && r.GetInt64(i) != 0; }

        return new Item
        {
            Id = r.GetInt64(r.GetOrdinal("id")),
            Title = S("title") ?? "",
            TitleNorm = S("title_norm") ?? "",
            ShopSubdomain = S("shop_subdomain"),
            ShopUuid = S("shop_uuid"),
            ShopUserId = L("shop_user_id"),
            ShopName = S("shop_name"),
            Url = S("url"),
            CanonicalUrl = S("canonical_url"),
            Thumb = S("thumb"),
            IsAdult = B("is_adult"),
            IsPlaceholder = B("is_placeholder"),
            IsEndOfSale = B("is_end_of_sale"),
            IsSoldOut = B("is_sold_out"),
            PriceText = S("price_text"),
            PriceValue = L("price_value"),
            Category = S("category"),
            CategoryParent = S("category_parent"),
            TagsJson = S("tags_json"),
            WishCount = L("wish_count"),
            PublishedAt = S("published_at"),
            DescShort = S("desc_short"),
            DescFull = S("desc_full"),
            SectionsJson = S("sections_json"),
            DescFetchedAt = S("desc_fetched_at"),
            FetchedAt = S("fetched_at"),
            FetchState = S("fetch_state") ?? "pending",
            FetchError = S("fetch_error"),
            CreatedAt = S("created_at") ?? ""
        };
    }

    public Item? Get(long id)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM items WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadItem(r) : null;
    }

    public bool Exists(long id)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM items WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() != null;
    }

    public int Count()
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM items";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    // ---------- 写入 ----------

    public void Upsert(Item it)
    {
        it.TitleNorm = TextNorm.Fingerprint(it.Title);
        if (string.IsNullOrEmpty(it.CreatedAt)) it.CreatedAt = DateTimeOffset.Now.ToString("O");

        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
INSERT INTO items (
  id, title, title_norm, shop_subdomain, shop_uuid, shop_user_id, shop_name,
  url, canonical_url, thumb, is_adult, is_placeholder, is_end_of_sale, is_sold_out,
  price_text, price_value, category, category_parent, tags_json, wish_count,
  published_at, desc_short, desc_full, sections_json, desc_fetched_at, fetched_at, fetch_state,
  fetch_error, created_at
) VALUES (
  $id, $title, $tn, $sub, $uuid, $uid, $sname,
  $url, $curl, $thumb, $adult, $ph, $eos, $sold,
  $ptext, $pval, $cat, $catp, $tags, $wish,
  $pub, $dshort, $dfull, $sections, $dfetch, $fetched, $state,
  $err, $created
)
ON CONFLICT(id) DO UPDATE SET
  title = excluded.title,
  title_norm = excluded.title_norm,
  shop_subdomain = excluded.shop_subdomain,
  shop_uuid = excluded.shop_uuid,
  shop_user_id = excluded.shop_user_id,
  shop_name = excluded.shop_name,
  url = excluded.url,
  canonical_url = excluded.canonical_url,
  thumb = COALESCE(excluded.thumb, items.thumb),
  is_adult = excluded.is_adult,
  is_placeholder = excluded.is_placeholder,
  is_end_of_sale = excluded.is_end_of_sale,
  is_sold_out = excluded.is_sold_out,
  price_text = excluded.price_text,
  price_value = excluded.price_value,
  category = excluded.category,
  category_parent = excluded.category_parent,
  tags_json = excluded.tags_json,
  wish_count = excluded.wish_count,
  published_at = excluded.published_at,
  desc_short = excluded.desc_short,
  desc_full = COALESCE(excluded.desc_full, items.desc_full),
  sections_json = COALESCE(excluded.sections_json, items.sections_json),
  desc_fetched_at = COALESCE(excluded.desc_fetched_at, items.desc_fetched_at),
  fetched_at = excluded.fetched_at,
  fetch_state = excluded.fetch_state,
  fetch_error = excluded.fetch_error;
""";
        cmd.Parameters.AddWithValue("$id", it.Id);
        cmd.Parameters.AddWithValue("$title", it.Title);
        cmd.Parameters.AddWithValue("$tn", it.TitleNorm);
        cmd.Parameters.AddWithValue("$sub", (object?)it.ShopSubdomain ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$uuid", (object?)it.ShopUuid ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$uid", (object?)it.ShopUserId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sname", (object?)it.ShopName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$url", (object?)it.Url ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$curl", (object?)it.CanonicalUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$thumb", (object?)it.Thumb ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$adult", it.IsAdult ? 1 : 0);
        cmd.Parameters.AddWithValue("$ph", it.IsPlaceholder ? 1 : 0);
        cmd.Parameters.AddWithValue("$eos", it.IsEndOfSale ? 1 : 0);
        cmd.Parameters.AddWithValue("$sold", it.IsSoldOut ? 1 : 0);
        cmd.Parameters.AddWithValue("$ptext", (object?)it.PriceText ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pval", (object?)it.PriceValue ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$cat", (object?)it.Category ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$catp", (object?)it.CategoryParent ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tags", (object?)it.TagsJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$wish", (object?)it.WishCount ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pub", (object?)it.PublishedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$dshort", (object?)it.DescShort ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$dfull", (object?)it.DescFull ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sections", (object?)it.SectionsJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$dfetch", (object?)it.DescFetchedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$fetched", (object?)it.FetchedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$state", it.FetchState);
        cmd.Parameters.AddWithValue("$err", (object?)it.FetchError ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$created", it.CreatedAt);
        cmd.ExecuteNonQuery();
    }

    public void SetThumb(long id, string? thumb)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE items SET thumb = $t WHERE id = $id";
        cmd.Parameters.AddWithValue("$t", (object?)thumb ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void SetFullDesc(long id, string? desc, string? sectionsJson)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE items SET desc_full = $d, sections_json = $s, desc_fetched_at = $at WHERE id = $id";
        cmd.Parameters.AddWithValue("$d", (object?)desc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$s", (object?)sectionsJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("O"));
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void MarkFailed(long id, string error)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE items SET fetch_state = 'failed', fetch_error = $e WHERE id = $id";
        cmd.Parameters.AddWithValue("$e", error);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    // ---------- 搜索 ----------

    private static string QuoteFts(string q) => "\"" + q.Replace("\"", "\"\"") + "\"";

    public (List<Item> Items, int Total) Search(string? q, int page, int size, string? sort, bool onlyLocal)
    {
        page = Math.Max(1, page);
        size = Math.Clamp(size, 1, 200);
        var offset = (page - 1) * size;

        var trimmed = q?.Trim() ?? "";
        var useFts = trimmed.Length >= 3 && _db.FtsMode == "trigram" && !long.TryParse(trimmed, out _);

        var from = useFts
            ? "FROM items i JOIN items_fts ON items_fts.rowid = i.id"
            : "FROM items i";

        var conds = new List<string>();
        var ps = new List<(string, object)>();

        if (useFts)
        {
            conds.Add("items_fts MATCH $q");
            ps.Add(("$q", QuoteFts(trimmed)));
        }
        else if (trimmed.Length > 0)
        {
            conds.Add("(i.title LIKE $lk OR IFNULL(i.shop_name,'') LIKE $lk OR IFNULL(i.tags_json,'') LIKE $lk OR CAST(i.id AS TEXT) LIKE $lk)");
            ps.Add(("$lk", "%" + trimmed + "%"));
        }

        if (onlyLocal) conds.Add("EXISTS (SELECT 1 FROM local_files f WHERE f.item_id = i.id)");

        var where = conds.Count > 0 ? " WHERE " + string.Join(" AND ", conds) : "";
        var order = sort switch
        {
            "wish" => " ORDER BY i.wish_count DESC NULLS LAST, i.id DESC",
            "title" => " ORDER BY i.title COLLATE NOCASE ASC",
            "shop" => " ORDER BY i.shop_name COLLATE NOCASE ASC, i.id DESC",
            _ => " ORDER BY i.fetched_at DESC NULLS LAST, i.id DESC"
        };

        using var c = _db.Open();

        int total;
        using (var cnt = c.CreateCommand())
        {
            cnt.CommandText = "SELECT COUNT(*) " + from + where;
            foreach (var (k, v) in ps) cnt.Parameters.AddWithValue(k, v);
            total = Convert.ToInt32(cnt.ExecuteScalar());
        }

        var list = new List<Item>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT i.* " + from + where + order + " LIMIT $size OFFSET $offset";
            foreach (var (k, v) in ps) cmd.Parameters.AddWithValue(k, v);
            cmd.Parameters.AddWithValue("$size", size);
            cmd.Parameters.AddWithValue("$offset", offset);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(ReadItem(r));
        }

        return (list, total);
    }

    /// <summary>匹配器用的轻量快照。</summary>
    public List<MatchItem> SnapshotForMatching(int limit)
    {
        var list = new List<MatchItem>();
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, title, title_norm FROM items LIMIT $lim";
        cmd.Parameters.AddWithValue("$lim", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var id = r.GetInt64(0);
            var title = r.IsDBNull(1) ? "" : r.GetString(1);
            var norm = r.IsDBNull(2) ? "" : r.GetString(2);
            if (string.IsNullOrEmpty(norm)) norm = TextNorm.Fingerprint(title);
            list.Add(new MatchItem(id, title, norm, TextNorm.Bigrams(norm)));
        }
        return list;
    }

    // ---------- local_files ----------

    public void UpsertLocalFile(LocalFile f)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
INSERT INTO local_files (path, root_path, name, kind, size, mtime, item_id, confidence,
                         matched_by, status, imported, updated_at)
VALUES ($path, $root, $name, $kind, $size, $mtime, $item, $conf, $by, $status, $imp, $now)
ON CONFLICT(path) DO UPDATE SET
  root_path = excluded.root_path,
  name = excluded.name,
  kind = excluded.kind,
  size = excluded.size,
  mtime = excluded.mtime,
  updated_at = excluded.updated_at;
""";
        cmd.Parameters.AddWithValue("$path", f.Path);
        cmd.Parameters.AddWithValue("$root", (object?)f.RootPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$name", f.Name);
        cmd.Parameters.AddWithValue("$kind", f.Kind);
        cmd.Parameters.AddWithValue("$size", f.Size);
        cmd.Parameters.AddWithValue("$mtime", (object?)f.Mtime ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$item", (object?)f.ItemId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$conf", (object?)f.Confidence ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$by", (object?)f.MatchedBy ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$status", f.Status);
        cmd.Parameters.AddWithValue("$imp", f.Imported ? 1 : 0);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public void SetFileLink(string path, long? itemId, double? confidence, string? matchedBy, string status, bool preserveManual = false)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
UPDATE local_files
SET item_id = $item, confidence = $conf, matched_by = $by, status = $status, updated_at = $now
WHERE path = $path AND ($preserve = 0 OR (COALESCE(matched_by,'') <> 'manual' AND status <> 'ignored'))
""";
        cmd.Parameters.AddWithValue("$preserve", preserveManual ? 1 : 0);
        cmd.Parameters.AddWithValue("$item", (object?)itemId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$conf", (object?)confidence ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$by", (object?)matchedBy ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
        cmd.Parameters.AddWithValue("$path", path);
        cmd.ExecuteNonQuery();
    }

    public void SetFileImported(string path, bool imported)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE local_files SET imported = $i WHERE path = $p";
        cmd.Parameters.AddWithValue("$i", imported ? 1 : 0);
        cmd.Parameters.AddWithValue("$p", path);
        cmd.ExecuteNonQuery();
    }

    public LocalFile? GetLocalFile(string path)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM local_files WHERE path = $p";
        cmd.Parameters.AddWithValue("$p", path);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadFile(r) : null;
    }

    private static LocalFile ReadFile(SqliteDataReader r)
    {
        string? S(string n) { var i = r.GetOrdinal(n); return r.IsDBNull(i) ? null : r.GetString(i); }
        long? L(string n) { var i = r.GetOrdinal(n); return r.IsDBNull(i) ? null : r.GetInt64(i); }
        double? D(string n) { var i = r.GetOrdinal(n); return r.IsDBNull(i) ? null : r.GetDouble(i); }
        bool B(string n) { var i = r.GetOrdinal(n); return !r.IsDBNull(i) && r.GetInt64(i) != 0; }

        return new LocalFile
        {
            Path = S("path") ?? "",
            RootPath = S("root_path"),
            Name = S("name") ?? "",
            Kind = S("kind") ?? "other",
            Size = L("size") ?? 0,
            Mtime = S("mtime"),
            ItemId = L("item_id"),
            Confidence = D("confidence"),
            MatchedBy = S("matched_by"),
            Status = S("status") ?? "unknown",
            Imported = B("imported"),
            UpdatedAt = S("updated_at")
        };
    }

    public (List<LocalFile> Files, int Total) ListFiles(string? status, int page, int size)
    {
        page = Math.Max(1, page);
        size = Math.Clamp(size, 1, 500);

        var where = string.IsNullOrWhiteSpace(status) || status == "all"
            ? ""
            : " WHERE f.status = $st";

        using var c = _db.Open();

        int total;
        using (var cnt = c.CreateCommand())
        {
            cnt.CommandText = "SELECT COUNT(*) FROM local_files f" + where;
            if (where.Length > 0) cnt.Parameters.AddWithValue("$st", status!);
            total = Convert.ToInt32(cnt.ExecuteScalar());
        }

        var list = new List<LocalFile>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = """
SELECT f.*, i.title AS j_title, i.thumb AS j_thumb, i.shop_name AS j_shop
FROM local_files f
LEFT JOIN items i ON i.id = f.item_id
""" + where + """
 ORDER BY (f.item_id IS NULL), f.updated_at DESC
LIMIT $size OFFSET $offset
""";
            if (where.Length > 0) cmd.Parameters.AddWithValue("$st", status!);
            cmd.Parameters.AddWithValue("$size", size);
            cmd.Parameters.AddWithValue("$offset", (page - 1) * size);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var f = ReadFile(r);
                var ti = r.GetOrdinal("j_title");
                if (!r.IsDBNull(ti)) { f.Title = r.GetString(ti); f.HasImage = true; }
                var th = r.GetOrdinal("j_thumb");
                if (!r.IsDBNull(th)) f.Thumb = r.GetString(th);
                var sh = r.GetOrdinal("j_shop");
                if (!r.IsDBNull(sh)) f.ShopName = r.GetString(sh);
                list.Add(f);
            }
        }
        return (list, total);
    }

    public LocalFile? GetFileByItem(long itemId)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM local_files WHERE item_id = $i ORDER BY updated_at DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$i", itemId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadFile(r) : null;
    }

    public List<LocalFile> ListFilesForItem(long itemId)
    {
        var list = new List<LocalFile>();
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM local_files WHERE item_id = $i ORDER BY updated_at DESC";
        cmd.Parameters.AddWithValue("$i", itemId);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadFile(r));
        return list;
    }

    // ---------- user_links ----------

    public void AddUserLink(string target, long itemId, string? note)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
INSERT INTO user_links (target, item_id, note, created_at)
VALUES ($t, $i, $n, $now)
ON CONFLICT(target) DO UPDATE SET item_id = excluded.item_id, note = excluded.note;
""";
        cmd.Parameters.AddWithValue("$t", target);
        cmd.Parameters.AddWithValue("$i", itemId);
        cmd.Parameters.AddWithValue("$n", (object?)note ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public Dictionary<string, long> ListUserLinks()
    {
        var d = new Dictionary<string, long>(StringComparer.Ordinal);
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT target, item_id FROM user_links";
        using var r = cmd.ExecuteReader();
        while (r.Read()) d[r.GetString(0)] = r.GetInt64(1);
        return d;
    }

    // ---------- import_jobs ----------

    public long CreateJob(long? itemId, string? localPath, string mode)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
INSERT INTO import_jobs (item_id, local_path, mode, state, created_at)
VALUES ($i, $p, $m, 'queued', $now);
SELECT last_insert_rowid();
""";
        cmd.Parameters.AddWithValue("$i", (object?)itemId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$p", (object?)localPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$m", mode);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public void UpdateJob(long id, string state, string? message, string? targetPath = null)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
UPDATE import_jobs
SET state = $s, message = $m,
    target_path = COALESCE($t, target_path),
    finished_at = CASE WHEN $s IN ('done','failed','unsupported','staged') THEN $now ELSE finished_at END
WHERE id = $id
""";
        cmd.Parameters.AddWithValue("$s", state);
        cmd.Parameters.AddWithValue("$m", (object?)message ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$t", (object?)targetPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public List<ImportJob> ListJobs(int limit = 50)
    {
        var list = new List<ImportJob>();
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
SELECT j.*, COALESCE(NULLIF(i.title,''), NULLIF(f.name,'')) AS j_title
FROM import_jobs j LEFT JOIN items i ON i.id = j.item_id
LEFT JOIN local_files f ON f.path = j.local_path
ORDER BY j.id DESC LIMIT $lim
""";
        cmd.Parameters.AddWithValue("$lim", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadJob(r));
        return list;
    }

    public ImportJob? GetJob(long id)
    {
        var list = ListJobsWhere("j.id = $v", id);
        return list.Count > 0 ? list[0] : null;
    }

    public List<ImportJob> ListJobsWhere(string cond, object value)
    {
        var list = new List<ImportJob>();
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
SELECT j.*, COALESCE(NULLIF(i.title,''), NULLIF(f.name,'')) AS j_title
FROM import_jobs j LEFT JOIN items i ON i.id = j.item_id
LEFT JOIN local_files f ON f.path = j.local_path
WHERE
""" + " " + cond + " ORDER BY j.id DESC LIMIT 100";
        cmd.Parameters.AddWithValue("$v", value);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadJob(r));
        return list;
    }

    private static ImportJob ReadJob(SqliteDataReader r)
    {
        var j = new ImportJob
        {
            Id = r.GetInt64(r.GetOrdinal("id")),
            ItemId = r.IsDBNull(r.GetOrdinal("item_id")) ? null : r.GetInt64(r.GetOrdinal("item_id")),
            LocalPath = r.IsDBNull(r.GetOrdinal("local_path")) ? null : r.GetString(r.GetOrdinal("local_path")),
            Mode = r.GetString(r.GetOrdinal("mode")),
            State = r.GetString(r.GetOrdinal("state")),
            Message = r.IsDBNull(r.GetOrdinal("message")) ? null : r.GetString(r.GetOrdinal("message")),
            TargetPath = r.IsDBNull(r.GetOrdinal("target_path")) ? null : r.GetString(r.GetOrdinal("target_path")),
            CreatedAt = r.GetString(r.GetOrdinal("created_at")),
            FinishedAt = r.IsDBNull(r.GetOrdinal("finished_at")) ? null : r.GetString(r.GetOrdinal("finished_at"))
        };
        var ti = r.GetOrdinal("j_title");
        j.Title = !r.IsDBNull(ti) ? r.GetString(ti) : j.LocalPath == null ? null : Path.GetFileName(j.LocalPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return j;
    }

    // ---------- app_state ----------
    public string? GetState(string key)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT value FROM app_state WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        var v = cmd.ExecuteScalar();
        return v is null or DBNull ? null : (string)v;
    }

    public void SetState(string key, string? value)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
INSERT INTO app_state (key, value) VALUES ($k, $v)
ON CONFLICT(key) DO UPDATE SET value = excluded.value
""";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", (object?)value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
}
