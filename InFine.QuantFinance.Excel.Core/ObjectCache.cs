using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace InFine.QuantFinance.Excel.Core
{
    /// <summary>What <c>QF.OBJ.INFO</c> and <c>QF.OBJ.LIST</c> report about a cached object.</summary>
    public sealed record ObjectInfo(
        string Handle,
        string Kind,
        string Name,
        int Version,
        string TypeName,
        string Description,
        DateTime CreatedUtc,
        IReadOnlyList<KeyValuePair<string, object>> Details);

    /// <summary>
    /// Registry of the library objects (curves, volatility surfaces...) that Excel cells refer to by handle.
    /// </summary>
    /// <remarks>
    /// A handle is the text <c>{kind}:{name}#{version}</c>, for example <c>QF.Curve:EUR6M#3</c>. Creating an object under
    /// a name already in use replaces it and bumps the version, so the handle text changes and every cell that
    /// takes the handle as an argument is recalculated by Excel. A handle whose version is no longer current is
    /// stale and is rejected rather than silently resolved to the newer object. Re-creating an object with the same
    /// <c>fingerprint</c> (identical inputs) keeps the existing version, so a full recalculation does not
    /// needlessly invalidate every dependent cell. Versions keep counting after a deletion, so a handle that was
    /// deleted can never match a later object of the same name. All members are thread-safe.
    /// </remarks>
    public sealed class ObjectCache
    {
        private sealed class Entry
        {
            public Entry(ObjectInfo info, object value, string? fingerprint)
            {
                Info = info;
                Value = value;
                Fingerprint = fingerprint;
            }

            public ObjectInfo Info { get; }
            public object Value { get; }
            public string? Fingerprint { get; }
        }

        private readonly object _gate = new();
        private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _versions = new(StringComparer.Ordinal);

        /// <summary>The cache shared by the Excel functions of the process.</summary>
        public static ObjectCache Default { get; } = new();

        public int Count
        {
            get { lock (_gate) return _entries.Count; }
        }

        /// <summary>
        /// Stores <paramref name="value"/> under <paramref name="kind"/> and <paramref name="name"/> and returns its handle.
        /// </summary>
        /// <param name="fingerprint">
        /// A canonical description of the inputs. If the current object of that name has the same fingerprint, it is
        /// kept (and its handle returned) instead of being replaced by an identical one.
        /// </param>
        public string Put(string kind, string name, object value, string? fingerprint = null, string description = "",
            IReadOnlyList<KeyValuePair<string, object>>? details = null)
        {
            ArgumentNullException.ThrowIfNull(value);
            ValidateKind(kind);
            ValidateName(name);
            string key = kind + ":" + name;
            lock (_gate)
            {
                if (fingerprint != null && _entries.TryGetValue(key, out var existing)
                    && existing.Fingerprint == fingerprint && existing.Value.GetType() == value.GetType())
                    return existing.Info.Handle;

                _versions.TryGetValue(key, out int version);
                version++;
                _versions[key] = version;
                string handle = key + "#" + version.ToString(CultureInfo.InvariantCulture);
                var info = new ObjectInfo(handle, kind, name, version, value.GetType().Name, description, DateTime.UtcNow,
                    details ?? Array.Empty<KeyValuePair<string, object>>());
                _entries[key] = new Entry(info, value, fingerprint);
                return handle;
            }
        }

        /// <summary>The object behind a handle, as a <typeparamref name="T"/>.</summary>
        /// <exception cref="XlException">
        /// #VALUE! when the text is not a handle or the object has another type; #REF! when it is unknown, deleted or stale.
        /// </exception>
        public T Get<T>(string handle) where T : class
        {
            var entry = Find(handle);
            if (entry.Value is T typed) return typed;
            throw new XlException(XlError.Value,
                $"{entry.Info.Handle} holds a {entry.Info.Kind} object ({entry.Info.TypeName}) but a {typeof(T).Name} is required here");
        }

        public bool TryGet<T>(string handle, out T? value) where T : class
        {
            try
            {
                value = Get<T>(handle);
                return true;
            }
            catch (XlException)
            {
                value = null;
                return false;
            }
        }

        /// <summary>Metadata of the object behind a handle (same errors as <see cref="Get{T}"/>, minus the type check).</summary>
        public ObjectInfo Info(string handle) => Find(handle).Info;

        /// <summary>Deletes the object behind a handle. A stale or unknown handle deletes nothing and returns false.</summary>
        public bool Delete(string handle)
        {
            if (!TryParseHandle(handle, out var kind, out var name, out var version)) return false;
            string key = kind + ":" + name;
            lock (_gate)
            {
                if (!_entries.TryGetValue(key, out var entry) || entry.Info.Version != version) return false;
                _entries.Remove(key);
                return true;
            }
        }

        /// <summary>All live objects, ordered by handle.</summary>
        public IReadOnlyList<ObjectInfo> List()
        {
            lock (_gate)
                return _entries.Values.Select(e => e.Info).OrderBy(i => i.Handle, StringComparer.Ordinal).ToArray();
        }

        /// <summary>Removes every object (version counters are kept).</summary>
        public void Clear()
        {
            lock (_gate) _entries.Clear();
        }

        /// <summary>Splits <c>{kind}:{name}#{version}</c>.</summary>
        public static bool TryParseHandle(string? handle, out string kind, out string name, out int version)
        {
            kind = name = "";
            version = 0;
            if (string.IsNullOrWhiteSpace(handle)) return false;
            int colon = handle.IndexOf(':');
            int hash = handle.LastIndexOf('#');
            if (colon <= 0 || hash <= colon + 1 || hash == handle.Length - 1) return false;
            if (!int.TryParse(handle.AsSpan(hash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out version) || version < 1)
                return false;
            kind = handle[..colon];
            name = handle[(colon + 1)..hash];
            return true;
        }

        private Entry Find(string handle)
        {
            if (!TryParseHandle(handle, out var kind, out var name, out var version))
                throw new XlException(XlError.Value,
                    $"'{handle}' is not an object handle (expected the text returned by a QF.* constructor, like QF.Curve:EUR6M#3)");
            string key = kind + ":" + name;
            lock (_gate)
            {
                if (!_entries.TryGetValue(key, out var entry))
                    throw new XlException(XlError.Ref,
                        $"no object '{key}' in the cache: it was never created in this session or has been deleted; recalculate the cell that creates it");
                if (entry.Info.Version != version)
                    throw new XlException(XlError.Ref,
                        $"handle {handle} is stale: '{key}' has since been replaced by version #{entry.Info.Version}");
                return entry;
            }
        }

        private static void ValidateKind(string kind)
        {
            if (string.IsNullOrWhiteSpace(kind) || kind.IndexOfAny(new[] { ':', '#' }) >= 0)
                throw new ArgumentException("An object kind must be non-empty and contain neither ':' nor '#'.", nameof(kind));
        }

        private static void ValidateName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new XlException(XlError.Value, "name: an object name is required");
            if (name.Contains('#') || name.Any(char.IsControl))
                throw new XlException(XlError.Value, $"name: '{name}' must not contain '#' or control characters");
        }
    }
}
