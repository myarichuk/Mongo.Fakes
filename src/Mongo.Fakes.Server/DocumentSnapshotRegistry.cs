using System.Collections.Generic;
using System.Linq;

namespace Mongo.Fakes.Server;

public sealed class DocumentSnapshotRegistry
{
    private readonly Dictionary<string, DocumentSnapshot> _snapshots = new();

    public void Register(string name, DocumentSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Snapshot name cannot be null or empty", nameof(name));
        if (snapshot == null)
            throw new ArgumentNullException(nameof(snapshot));

        _snapshots[name] = snapshot;
    }

    public DocumentSnapshot? Get(string name)
    {
        return _snapshots.TryGetValue(name, out var snapshot) ? snapshot : null;
    }

    public IEnumerable<DocumentSnapshot> GetDirty()
    {
        return _snapshots.Values.Where(s => s.IsDirty);
    }

    public string GetSummary()
    {
        if (_snapshots.Count == 0)
            return "(empty registry)";

        var lines = _snapshots.Values.Select(s => s.GetDebugInfo());
        return string.Join(Environment.NewLine, lines);
    }
}
