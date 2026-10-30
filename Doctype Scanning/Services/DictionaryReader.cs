using Newtonsoft.Json;
using System.IO;

namespace Doctype_Scanning.Services;

/// <summary>
/// Reads a dictionary file: { "ProjectId": n, "DictionaryCollection": [ { "DictionaryName": "...", "DictionaryValues": [ { "Name": "...", "Value": "..." } ] } ] }.
/// The file is large (tens of MB), so it is streamed instead of loaded into one object.
/// Both Name and Value are accepted as valid values (they are the same for most dictionaries; for Header Language Name is the code).
/// </summary>
public static class DictionaryReader
{
    public static DictionaryLibrary Read(string file)
    {
        DictionaryLibrary library = new() { FilePath = file };

        using StreamReader stream = new(file);
        using JsonTextReader reader = new(stream);

        WordList? current = null;
        string? pendingName = null;

        while (reader.Read())
        {
            if (reader.TokenType != JsonToken.PropertyName) continue;

            switch (reader.Value as string)
            {
                case "DictionaryName":
                    Flush(library, ref current, ref pendingName);
                    current = new WordList(reader.ReadAsString() ?? string.Empty);
                    break;

                case "Name":
                    // a Name without a Value after it still counts
                    if (pendingName != null) current?.Add(pendingName);
                    pendingName = reader.ReadAsString();
                    break;

                case "Value":
                    string? value = reader.ReadAsString();
                    if (value != null) current?.Add(value);
                    if (pendingName != null) current?.Add(pendingName);
                    pendingName = null;
                    break;
            }
        }

        Flush(library, ref current, ref pendingName);

        if (library.Count == 0)
            throw new InvalidDataException("No dictionaries were found in the file. Expected a 'DictionaryCollection' with 'DictionaryName' and 'DictionaryValues'.");

        return library;
    }

    private static void Flush(DictionaryLibrary library, ref WordList? current, ref string? pendingName)
    {
        if (current == null) return;

        if (pendingName != null) current.Add(pendingName);
        pendingName = null;

        if (current.Name.Length > 0) library.Add(current.Name, current);
        current = null;
    }
}
