using Doctype_Scanning.Models;
using Microsoft.Data.Sqlite;
using System.IO;

namespace Doctype_Scanning.Services.Qc;

/// <summary>Reads a db3 for the QC: header values from the Header table, record fields from the Record table.</summary>
internal static class QcDb3Reader
{
    public static QcBatch Read(string file, string batchName, QcFieldMap map)
    {
        QcBatch batch = new() { Name = batchName };

        SqliteConnectionStringBuilder csb = new() { DataSource = file, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        using SqliteConnection conn = new(csb.ToString());
        conn.Open();

        HashSet<string> tables = Db3Reader.GetTables(conn);
        foreach (string required in new[] { "Image", "Header", "Record" })
        {
            if (!tables.Contains(required))
                throw new InvalidDataException($"Not a valid db3: table '{required}' is missing");
        }

        Dictionary<long, QcImage> images = new();
        using (SqliteCommand cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT ImageID, ImageName, DocType FROM Image ORDER BY ImageID";
            using SqliteDataReader reader = cmd.ExecuteReader();

            while (reader.Read())
            {
                QcImage image = new()
                {
                    Name = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                    DocType = reader.IsDBNull(2) ? string.Empty : reader.GetString(2).Trim()
                };

                images[reader.GetInt64(0)] = image;
                batch.Images.Add(image);
            }
        }

        // header values, one header row per image section
        Dictionary<string, List<string>> headerCols = ColumnsOf(map, QcFields.Header, Db3Reader.GetColumns(conn, "Header"), batch);
        if (headerCols.Count > 0)
        {
            string columns = string.Join(", ", headerCols.Values.SelectMany(c => c).Distinct().Select(Db3Reader.Quote));
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT ImageID, Section, {columns} FROM Header";
            using SqliteDataReader reader = cmd.ExecuteReader();

            string[] selected = headerCols.Values.SelectMany(c => c).Distinct().ToArray();

            while (reader.Read())
            {
                if (!images.TryGetValue(reader.GetInt64(0), out QcImage? image)) continue;

                string section = reader.IsDBNull(1) ? string.Empty : Convert.ToString(reader.GetValue(1)) ?? string.Empty;

                foreach ((string label, List<string> cols) in headerCols)
                {
                    string value = FirstFilled(reader, 2, selected, cols);
                    image.AddHeader(label, section, value);
                    if (value.Trim().Length > 0) image.HasKeyedData = true;
                }
            }
        }

        // records and the record fields
        Dictionary<string, List<string>> recordCols = ColumnsOf(map, QcFields.Record, Db3Reader.GetColumns(conn, "Record"), batch);
        string[] recordSelected = recordCols.Values.SelectMany(c => c).Distinct().ToArray();
        string recordSelect = recordSelected.Length == 0 ? string.Empty : ", " + string.Join(", ", recordSelected.Select(c => $"r.{Db3Reader.Quote(c)}"));

        using (SqliteCommand cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"SELECT h.ImageID{recordSelect} FROM Record r JOIN Header h ON h.HeaderID = r.HeaderID";
            using SqliteDataReader reader = cmd.ExecuteReader();

            while (reader.Read())
            {
                if (!images.TryGetValue(reader.GetInt64(0), out QcImage? image)) continue;

                image.RecordCount++;
                image.HasKeyedData = true;

                foreach ((string label, List<string> cols) in recordCols)
                {
                    string value = FirstFilled(reader, 1, recordSelected, cols);
                    if (value.Trim().Length > 0) image.RecordValues.TryAdd(label, value);
                }
            }
        }

        return batch;
    }

    /// <summary>
    /// Label -> the db3 columns that hold it (edited value first, then original). A field that is not in the table
    /// is left out and the reviewer is told.
    /// </summary>
    private static Dictionary<string, List<string>> ColumnsOf(QcFieldMap map, string[] labels, HashSet<string> tableColumns, QcBatch batch)
    {
        Dictionary<string, List<string>> result = new();

        foreach (string label in labels)
        {
            CiField? field = map.Get(label);
            if (field == null) continue;

            List<string> cols = new[] { $"{field.Name}_edt", $"{field.Name}_orig", field.Name }.Where(tableColumns.Contains).ToList();

            if (cols.Count == 0) batch.Notes.Add($"Column of '{label}' ({field.Name}) was not found in the db3 - its checks were skipped.");
            else result[label] = cols;
        }

        return result;
    }

    /// <summary>First non-empty value among a field's columns, read from the selected-column list.</summary>
    private static string FirstFilled(SqliteDataReader reader, int firstColumn, string[] selected, List<string> cols)
    {
        foreach (string col in cols)
        {
            int index = firstColumn + Array.IndexOf(selected, col);
            if (reader.IsDBNull(index)) continue;

            string value = Convert.ToString(reader.GetValue(index), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            if (value.Length > 0) return value;
        }

        return string.Empty;
    }
}
