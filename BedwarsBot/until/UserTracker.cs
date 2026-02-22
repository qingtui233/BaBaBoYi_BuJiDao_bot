using Microsoft.Data.Sqlite;
using System.IO;

namespace BedwarsBot;

public class UserTracker
{
    private readonly string _dbPath;

    public UserTracker()
    {
        // 数据库存放在 pz 文件夹下
        string folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "pz");
        if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
        
        _dbPath = Path.Combine(folder, "users.db");
        InitDb();
    }

    private void InitDb()
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        var cmd = conn.CreateCommand();
        // 建表：记录 QQ 用户的 ID
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS sess_users (user_id TEXT PRIMARY KEY)";
        cmd.ExecuteNonQuery();
    }

    // 检查并记录是否为第一次使用
    public bool CheckAndMarkFirstTime(string userId)
    {
        if (string.IsNullOrEmpty(userId)) return false;

        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        
        var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = "SELECT COUNT(1) FROM sess_users WHERE user_id = $id";
        checkCmd.Parameters.AddWithValue("$id", userId);
        long count = (long)checkCmd.ExecuteScalar();

        if (count == 0)
        {
            // 第一次用，打上标记
            var insertCmd = conn.CreateCommand();
            insertCmd.CommandText = "INSERT INTO sess_users (user_id) VALUES ($id)";
            insertCmd.Parameters.AddWithValue("$id", userId);
            insertCmd.ExecuteNonQuery();
            return true; 
        }
        
        return false; // 以前用过了
    }
}