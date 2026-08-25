using MongoDB.Bson;

namespace Mongo.Fakes.Server;

/// <summary>
/// Immutable specification for a text index: field names and wildcard indicator.
/// </summary>
public record TextIndexSpec(IReadOnlyList<string> Fields, bool IsWildcard)
{
    /// <summary>
    /// Attempts to create a TextIndexSpec from a MongoDB index key document.
    /// Returns null if the document contains no "text" field values.
    /// Only one text index per collection is supported.
    /// </summary>
    public static TextIndexSpec? TryCreate(BsonDocument keyDoc)
    {
        var textFields = new List<string>();
        bool isWildcard = false;

        foreach (var element in keyDoc)
        {
            if (element.Value is BsonString { Value: "text" })
            {
                if (element.Name == "$**")
                {
                    isWildcard = true;
                }
                else
                {
                    textFields.Add(element.Name);
                }
            }
        }

        // Return null if no text index found
        if (textFields.Count == 0 && !isWildcard)
        {
            return null;
        }

        return new TextIndexSpec(textFields.AsReadOnly(), isWildcard);
    }
}
