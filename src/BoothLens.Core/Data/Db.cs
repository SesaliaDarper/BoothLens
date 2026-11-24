using Microsoft.Data.Sqlite;
using System.Text.RegularExpressions;

namespace BoothLens.Core.Data;

public sealed class Db
{
    public string DbPath { get; }
    private readonly string _connStr;

    /// <summary>"trigram" / "plain" / "off" —— 取决于本机 SQLite 是否带 trigram 分词器。</summary>
    public string FtsMode { get; private set; } = "off";

    public Db(string dbPath)
    {
        DbPath = dbPath;
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _connStr = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();
    }

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_connStr);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;";
        cmd.ExecuteNonQuery();
        return c;
    }

    private const string BaseSchema = """
CREATE TABLE IF NOT EXISTS items (
  id              INTEGER PRIMARY KEY,
  title           TEXT NOT NULL DEFAULT '',
  title_norm      TEXT NOT NULL DEFAULT '',
  shop_subdomain  TEXT,
  shop_uuid       TEXT,
  shop_user_id    INTEGER,
  shop_name       TEXT,
  url             TEXT,
  canonical_url   TEXT,
  thumb           TEXT,
  is_adult        INTEGER NOT NULL DEFAULT 0,
  is_placeholder  INTEGER NOT NULL DEFAULT 0,
  is_end_of_sale  INTEGER NOT NULL DEFAULT 0,
  is_sold_out     INTEGER NOT NULL DEFAULT 0,
  price_text      TEXT,
  price_value     INTEGER,
  category        TEXT,
  category_parent TEXT,
  tags_json       TEXT,
  wish_count      INTEGER,
  published_at    TEXT,
  desc_short      TEXT,
  desc_full       TEXT,
  sections_json   TEXT,
  desc_fetched_at TEXT,
  fetched_at      TEXT,
  fetch_state     TEXT NOT NULL DEFAULT 'pending',
  fetch_error     TEXT,
  created_at      TEXT NOT NULL DEFAULT ''
);
CREATE INDEX IF NOT EXISTS ix_items_shop ON items(shop_subdomain);
CREATE INDEX IF NOT EXISTS ix_items_state ON items(fetch_state);

CREATE TABLE IF NOT EXISTS local_files (
  path       TEXT PRIMARY KEY,
  root_path  TEXT,
  name       TEXT NOT NULL DEFAULT '',
  kind       TEXT NOT NULL DEFAULT 'other',
  size       INTEGER NOT NULL DEFAULT 0,
  mtime      TEXT,
  item_id    INTEGER,
  confidence REAL,
  matched_by TEXT,
  status     TEXT NOT NULL DEFAULT 'unknown',
  imported   INTEGER NOT NULL DEFAULT 0,
  updated_at TEXT
);
CREATE INDEX IF NOT EXISTS ix_files_status ON local_files(status);
CREATE INDEX IF NOT EXISTS ix_files_item ON local_files(item_id);

CREATE TABLE IF NOT EXISTS user_links (
  target     TEXT PRIMARY KEY,
  item_id    INTEGER NOT NULL,
  note       TEXT,
  created_at TEXT NOT NULL DEFAULT ''
);

CREATE TABLE IF NOT EXISTS import_jobs (
  id          INTEGER PRIMARY KEY AUTOINCREMENT,
  item_id     INTEGER,
  local_path  TEXT,
  mode        TEXT NOT NULL DEFAULT 'copy',
  state       TEXT NOT NULL DEFAULT 'queued',
  message     TEXT,
  target_path TEXT,
  created_at  TEXT NOT NULL DEFAULT '',
  finished_at TEXT
);
CREATE INDEX IF NOT EXISTS ix_jobs_state ON import_jobs(state);

CREATE TABLE IF NOT EXISTS app_state (
  key   TEXT PRIMARY KEY,
  value TEXT
);
""";

    public void EnsureSchema()
    {
        using var c = Open();

        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = BaseSchema;
            cmd.ExecuteNonQuery();
        }

        // 轻量迁移：老库补列
        TryAddColumn(c, "items", "sections_json", "TEXT");
        MakeJobItemOptional(c);

        // FTS5 是可选增强：trigram 能搜中文/日文子串，没有就退化成 LIKE。
        foreach (var tok in new[] { "trigram", "unicode61" })
        {
            try
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = $"""
CREATE VIRTUAL TABLE IF NOT EXISTS items_fts USING fts5(
  title, shop_name, tags, tokenize='{tok}'
);
""";
                cmd.ExecuteNonQuery();
                FtsMode = tok;
                break;
            }
            catch
            {
                // 下一个候选
            }
        }

        if (FtsMode == "off") return;

        using var trig = c.CreateCommand();
        trig.CommandText = """
CREATE TRIGGER IF NOT EXISTS items_ai AFTER INSERT ON items BEGIN
  INSERT INTO items_fts(rowid, title, shop_name, tags)
  VALUES (new.id, new.title, coalesce(new.shop_name,''), coalesce(new.tags_json,''));
END;
CREATE TRIGGER IF NOT EXISTS items_au AFTER UPDATE ON items BEGIN
  DELETE FROM items_fts WHERE rowid = old.id;
  INSERT INTO items_fts(rowid, title, shop_name, tags)
  VALUES (new.id, new.title, coalesce(new.shop_name,''), coalesce(new.tags_json,''));
END;
CREATE TRIGGER IF NOT EXISTS items_ad AFTER DELETE ON items BEGIN
  DELETE FROM items_fts WHERE rowid = old.id;
END;
""";
        trig.ExecuteNonQuery();
    }

    /// <summary>Rebuild only legacy NOT NULL job schemas; preserve rows, FKs, indexes, triggers and IDs.</summary>
    private static void MakeJobItemOptional(SqliteConnection c)
    {
        using (var info = c.CreateCommand())
        {
            info.CommandText = "PRAGMA table_info(import_jobs)";
            using var r = info.ExecuteReader();
            var required = false;
            while (r.Read()) if (r.GetString(1) == "item_id") required = r.GetInt64(3) != 0;
            if (!required) return;
        }
        string schema;
        var objects = new List<string>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='import_jobs'";
            schema = (string)cmd.ExecuteScalar()!;
            cmd.CommandText = "SELECT sql FROM sqlite_master WHERE tbl_name='import_jobs' AND type IN ('index','trigger') AND sql IS NOT NULL";
            using var r = cmd.ExecuteReader(); while (r.Read()) objects.Add(r.GetString(0));
        }
        // Retain any legacy FK/extra columns instead of replacing the table with a lossy fixed schema.
        var optional = Regex.Replace(schema, "(?<column>(?:\\bitem_id\\b|\"item_id\"|`item_id`|\\[item_id\\])\\s+INTEGER\\s+)NOT\\s+NULL\\b", "${column}", RegexOptions.IgnoreCase);
        if (optional == schema) throw new InvalidOperationException("Legacy import_jobs.item_id definition is unsupported.");
        var create = Regex.Replace(optional, "^CREATE\\s+TABLE\\s+(?:IF\\s+NOT\\s+EXISTS\\s+)?(?:import_jobs|\"import_jobs\"|`import_jobs`|\\[import_jobs\\])", "CREATE TABLE import_jobs_nullable", RegexOptions.IgnoreCase);
        using var pragma = c.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys";
        var foreignKeys = Convert.ToInt64(pragma.ExecuteScalar()) != 0;
        pragma.CommandText = "PRAGMA foreign_keys=OFF"; pragma.ExecuteNonQuery();
        try
        {
            using var transaction = c.BeginTransaction();
            using var cmd = c.CreateCommand(); cmd.Transaction = transaction;
            // Preserve AUTOINCREMENT's high-water mark, including deleted historical job IDs.
            cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE name='sqlite_sequence'";
            object? sequence = null;
            if (cmd.ExecuteScalar() != null)
            {
                cmd.CommandText = "SELECT seq FROM sqlite_sequence WHERE name='import_jobs'";
                sequence = cmd.ExecuteScalar();
            }
            cmd.CommandText = create; cmd.ExecuteNonQuery();
            cmd.CommandText = "INSERT INTO import_jobs_nullable SELECT * FROM import_jobs; DROP TABLE import_jobs; ALTER TABLE import_jobs_nullable RENAME TO import_jobs;";
            cmd.ExecuteNonQuery();
            foreach (var sql in objects) { cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
            if (sequence is not null and not DBNull)
            {
                cmd.CommandText = "UPDATE sqlite_sequence SET seq=MAX(seq,$seq) WHERE name='import_jobs'";
                cmd.Parameters.AddWithValue("$seq", sequence); cmd.ExecuteNonQuery();
            }
            cmd.CommandText = "PRAGMA foreign_key_check";
            using (var r = cmd.ExecuteReader()) if (r.Read()) throw new InvalidOperationException("Foreign key check failed during import_jobs migration.");
            transaction.Commit();
        }
        finally
        {
            pragma.CommandText = foreignKeys ? "PRAGMA foreign_keys=ON" : "PRAGMA foreign_keys=OFF";
            pragma.ExecuteNonQuery();
        }
    }

    private static void TryAddColumn(SqliteConnection c, string table, string column, string type)
    {
        try
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type};";
            cmd.ExecuteNonQuery();
        }
        catch
        {
            // 列已存在
        }
    }

    /// <summary>FTS 索引重建（换过分词器或历史数据缺失时用）。</summary>
    public void RebuildFts()
    {
        if (FtsMode == "off") return;
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM items_fts;";
        cmd.ExecuteNonQuery();
        using var cmd2 = c.CreateCommand();
        cmd2.CommandText = """
INSERT INTO items_fts(rowid, title, shop_name, tags)
SELECT id, title, coalesce(shop_name,''), coalesce(tags_json,'') FROM items;
""";
        cmd2.ExecuteNonQuery();
    }
}
