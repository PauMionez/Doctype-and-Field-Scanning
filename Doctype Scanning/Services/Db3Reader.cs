using Doctype_Scanning.Models;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO;

namespace Doctype_Scanning.Services;

/// <summary>Reads the images, record counts and the checked field out of one db3.</summary>
internal static class Db3Reader
{
    private enum FieldLevel { None, Header, Record }

    public static SourceSnapshot Read(string file, string sourceName, CiField field)
    {
        SourceSnapshot snapshot = new();

        SqliteConnectionStringBuilder csb = new() { DataSource = file, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        using SqliteConnection conn = new(csb.ToString());
        conn.Open();

        HashSet<string> tables = GetTables(conn);
        foreach (string required in new[] { "Image", "Header", "Record" })
        {
            if (!tables.Contains(required))
                throw new InvalidDataException($"Not a valid db3: table '{required}' is missing");
        }

        if (tables.Contains("DocType"))
            snapshot.DocTypeTableEntries.AddRange(ReadStrings(conn, "SELECT Type FROM DocType"));

        (FieldLevel level, List<string> valueCols) = LocateField(field, GetColumns(conn, "Header"), GetColumns(conn, "Record"));

        if (level == FieldLevel.None)
        {
            snapshot.FieldProblem = $"[Typo] Column of field '{field.Label}' ({field.Name}) was not found in the Header or Record table - the field was not checked";
        }

        snapshot.FieldIsHeader = level == FieldLevel.Header;
        snapshot.Images.AddRange(ReadImages(conn));
        foreach ((long imageId, RecordStats stats) in ReadRecordStats(conn))
            snapshot.RecordStats[imageId] = stats;

        Dictionary<long, string> imageNames = snapshot.Images.ToDictionary(i => i.Id, i => i.Name);

        if (level == FieldLevel.Header)
        {
            string columns = string.Join(", ", valueCols.Select(c => Quote(c)));

            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT ImageID, {columns} FROM Header";
            using SqliteDataReader reader = cmd.ExecuteReader();

            while (reader.Read())
            {
                long imageId = reader.GetInt64(0);
                string? value = PickValue(reader, 1, valueCols.Count);
                if (value == null) continue;

                snapshot.HeaderValues.TryAdd(imageId, value);

                long? recordId = snapshot.RecordStats.TryGetValue(imageId, out RecordStats stats) ? stats.FirstId : null;
                snapshot.Values.Add(new ValueEntry(sourceName, imageNames.GetValueOrDefault(imageId, string.Empty), recordId, value));
            }
        }
        else if (level == FieldLevel.Record)
        {
            string columns = string.Join(", ", valueCols.Select(c => $"r.{Quote(c)}"));

            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT h.ImageID, r.RecordID, {columns} FROM Record r JOIN Header h ON h.HeaderID = r.HeaderID";
            using SqliteDataReader reader = cmd.ExecuteReader();

            while (reader.Read())
            {
                string? value = PickValue(reader, 2, valueCols.Count);
                if (value == null) continue;

                long imageId = reader.GetInt64(0);
                snapshot.Values.Add(new ValueEntry(sourceName, imageNames.GetValueOrDefault(imageId, string.Empty), reader.GetInt64(1), value));
            }
        }

        return snapshot;
    }

    /// <summary>
    /// Finds where the field lives. The CI's "Header Field" flag decides which table is tried first.
    /// The edited value is preferred over the original one, like the other db3 tools.
    /// </summary>
    private static (FieldLevel Level, List<string> Columns) LocateField(CiField field, HashSet<string> headerCols, HashSet<string> recordCols)
    {
        List<string> InTable(HashSet<string> cols) =>
            new[] { $"{field.Name}_edt", $"{field.Name}_orig", field.Name }.Where(cols.Contains).ToList();

        List<string> header = InTable(headerCols);
        List<string> record = InTable(recordCols);

        if (field.IsHeader)
        {
            if (header.Count > 0) return (FieldLevel.Header, header);
            if (record.Count > 0) return (FieldLevel.Record, record);
        }
        else
        {
            if (record.Count > 0) return (FieldLevel.Record, record);
            if (header.Count > 0) return (FieldLevel.Header, header);
        }

        return (FieldLevel.None, new List<string>());
    }

    /// <summary>First non-empty value among the selected columns (edited, then original).</summary>
    internal static string? PickValue(SqliteDataReader reader, int firstColumn, int columnCount)
    {
        for (int i = 0; i < columnCount; i++)
        {
            if (reader.IsDBNull(firstColumn + i)) continue;

            string value = Convert.ToString(reader.GetValue(firstColumn + i), CultureInfo.InvariantCulture) ?? string.Empty;
            if (value.Length > 0) return value;
        }

        return null;
    }

    private static List<ImageRow> ReadImages(SqliteConnection conn)
    {
        List<ImageRow> images = new();

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT ImageID, ImageName, DocType FROM Image ORDER BY ImageID";
        using SqliteDataReader reader = cmd.ExecuteReader();

        while (reader.Read())
        {
            images.Add(new ImageRow(
                reader.GetInt64(0),
                reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        return images;
    }

    private static List<(long ImageId, RecordStats Stats)> ReadRecordStats(SqliteConnection conn)
    {
        List<(long, RecordStats)> stats = new();

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT h.ImageID, COUNT(r.RecordID), MIN(r.RecordID)
                            FROM Record r
                            JOIN Header h ON h.HeaderID = r.HeaderID
                            GROUP BY h.ImageID";
        using SqliteDataReader reader = cmd.ExecuteReader();

        while (reader.Read())
            stats.Add((reader.GetInt64(0), new RecordStats(reader.GetInt64(1), reader.GetInt64(2))));

        return stats;
    }

    internal static HashSet<string> GetTables(SqliteConnection conn) =>
        ReadStrings(conn, "SELECT name FROM sqlite_master WHERE type = 'table'").ToHashSet(StringComparer.OrdinalIgnoreCase);

    internal static HashSet<string> GetColumns(SqliteConnection conn, string table)
    {
        HashSet<string> columns = new(StringComparer.OrdinalIgnoreCase);

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({Quote(table)})";
        using SqliteDataReader reader = cmd.ExecuteReader();

        // names that go into SQL later only ever come from this list
        while (reader.Read()) columns.Add((string)reader["name"]);

        return columns;
    }

    private static List<string> ReadStrings(SqliteConnection conn, string sql)
    {
        List<string> list = new();

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using SqliteDataReader reader = cmd.ExecuteReader();

        while (reader.Read())
        {
            if (!reader.IsDBNull(0)) list.Add(reader.GetString(0));
        }

        return list;
    }

    internal static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";
}
