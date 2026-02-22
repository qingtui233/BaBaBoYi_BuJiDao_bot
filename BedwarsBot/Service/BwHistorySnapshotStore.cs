using Microsoft.Data.Sqlite;
using System.Globalization;

namespace BedwarsBot;

public sealed class BwHistorySnapshotStore
{
    private readonly string _dbPath;
    private readonly object _lock = new();

    public BwHistorySnapshotStore(string dbPath)
    {
        _dbPath = dbPath;
    }

    public void Initialize()
    {
        lock (_lock)
        {
            var dbDir = Path.GetDirectoryName(_dbPath);
            if (!string.IsNullOrWhiteSpace(dbDir))
            {
                Directory.CreateDirectory(dbDir);
            }

            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS bw_daily_snapshots (
                    player_name TEXT NOT NULL COLLATE NOCASE,
                    display_name TEXT NOT NULL,
                    record_date TEXT NOT NULL,
                    captured_at_local TEXT NOT NULL,
                    source TEXT NOT NULL,
                    json_response TEXT NOT NULL,
                    player_uuid TEXT,
                    PRIMARY KEY (player_name, record_date)
                );
                CREATE INDEX IF NOT EXISTS idx_bw_daily_snapshots_record_date ON bw_daily_snapshots (record_date);";
            cmd.ExecuteNonQuery();
        }
    }

    public void UpsertSnapshot(
        string playerName,
        string displayName,
        string jsonResponse,
        DateTimeOffset capturedAtLocal,
        string source,
        string? playerUuid)
    {
        if (string.IsNullOrWhiteSpace(playerName) || string.IsNullOrWhiteSpace(jsonResponse))
        {
            return;
        }

        var keyName = playerName.Trim();
        var safeDisplayName = string.IsNullOrWhiteSpace(displayName) ? keyName : displayName.Trim();
        var recordDate = capturedAtLocal.LocalDateTime.ToString("yyyy-MM-dd");
        var capturedText = capturedAtLocal.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
        var safeSource = string.IsNullOrWhiteSpace(source) ? "manual" : source.Trim().ToLowerInvariant();

        lock (_lock)
        {
            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();

            string? existingCapturedAt;
            using (var checkCmd = conn.CreateCommand())
            {
                checkCmd.CommandText = @"
                    SELECT captured_at_local
                    FROM bw_daily_snapshots
                    WHERE player_name = $name AND record_date = $date
                    LIMIT 1";
                checkCmd.Parameters.AddWithValue("$name", keyName);
                checkCmd.Parameters.AddWithValue("$date", recordDate);
                existingCapturedAt = checkCmd.ExecuteScalar() as string;
            }

            if (!string.IsNullOrWhiteSpace(existingCapturedAt)
                && string.CompareOrdinal(existingCapturedAt, capturedText) >= 0)
            {
                return;
            }

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO bw_daily_snapshots
                (player_name, display_name, record_date, captured_at_local, source, json_response, player_uuid)
                VALUES ($name, $display, $date, $captured, $source, $json, $uuid)
                ON CONFLICT(player_name, record_date) DO UPDATE SET
                    display_name = excluded.display_name,
                    captured_at_local = excluded.captured_at_local,
                    source = excluded.source,
                    json_response = excluded.json_response,
                    player_uuid = excluded.player_uuid";
            cmd.Parameters.AddWithValue("$name", keyName);
            cmd.Parameters.AddWithValue("$display", safeDisplayName);
            cmd.Parameters.AddWithValue("$date", recordDate);
            cmd.Parameters.AddWithValue("$captured", capturedText);
            cmd.Parameters.AddWithValue("$source", safeSource);
            cmd.Parameters.AddWithValue("$json", jsonResponse);
            cmd.Parameters.AddWithValue("$uuid", (object?)playerUuid ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    public bool HasSnapshotForDate(string playerName, DateOnly date)
    {
        if (string.IsNullOrWhiteSpace(playerName))
        {
            return false;
        }

        var keyName = playerName.Trim();
        var dateText = date.ToString("yyyy-MM-dd");
        lock (_lock)
        {
            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT 1
                FROM bw_daily_snapshots
                WHERE player_name = $name AND record_date = $date
                LIMIT 1";
            cmd.Parameters.AddWithValue("$name", keyName);
            cmd.Parameters.AddWithValue("$date", dateText);
            var scalar = cmd.ExecuteScalar();
            return scalar != null && scalar != DBNull.Value;
        }
    }

    public bool TryGetSnapshot(string playerName, DateOnly date, out BwDailySnapshot snapshot)
    {
        snapshot = default;
        if (string.IsNullOrWhiteSpace(playerName))
        {
            return false;
        }

        var keyName = playerName.Trim();
        var dateText = date.ToString("yyyy-MM-dd");
        lock (_lock)
        {
            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT player_name, display_name, record_date, captured_at_local, source, json_response, player_uuid
                FROM bw_daily_snapshots
                WHERE player_name = $name AND record_date = $date
                LIMIT 1";
            cmd.Parameters.AddWithValue("$name", keyName);
            cmd.Parameters.AddWithValue("$date", dateText);

            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                return false;
            }

            var capturedText = reader.GetString(3);
            var capturedAtLocal = DateTime.TryParseExact(
                capturedText,
                "yyyy-MM-dd HH:mm:ss",
                CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out var parsedLocal)
                ? parsedLocal
                : DateTime.Now;

            snapshot = new BwDailySnapshot(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                capturedAtLocal,
                reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? string.Empty : reader.GetString(6));
            return true;
        }
    }
}

public readonly record struct BwDailySnapshot(
    string PlayerName,
    string DisplayName,
    string RecordDate,
    DateTime CapturedAtLocal,
    string Source,
    string JsonResponse,
    string PlayerUuid);
