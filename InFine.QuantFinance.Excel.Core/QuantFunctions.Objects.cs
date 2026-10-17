using System;
using System.Globalization;

namespace InFine.QuantFinance.Excel.Core
{
    public sealed partial class QuantFunctions
    {
        private static readonly string[] ListHeaders = { "Handle", "Kind", "Name", "Version", "Type", "Created (UTC)", "Description" };

        /// <summary>QF.OBJ.LIST() - a header row followed by one row per live object.</summary>
        public object ObjList() => Run("QF.OBJ.LIST", () =>
        {
            var items = Cache.List();
            var result = new object[items.Count + 1, ListHeaders.Length];
            for (int j = 0; j < ListHeaders.Length; j++) result[0, j] = ListHeaders[j];
            for (int i = 0; i < items.Count; i++)
            {
                var info = items[i];
                result[i + 1, 0] = info.Handle;
                result[i + 1, 1] = info.Kind;
                result[i + 1, 2] = info.Name;
                result[i + 1, 3] = (double)info.Version;
                result[i + 1, 4] = info.TypeName;
                result[i + 1, 5] = Timestamp(info.CreatedUtc);
                result[i + 1, 6] = info.Description;
            }
            return result;
        });

        /// <summary>QF.OBJ.INFO(handle) - a two-column table of property names and values.</summary>
        public object ObjInfo(object? handle) => Run("QF.OBJ.INFO", () =>
        {
            var info = Cache.Info(Arg.Text(handle, "handle"));
            var rows = new System.Collections.Generic.List<(string, object)>
            {
                ("Handle", info.Handle),
                ("Kind", info.Kind),
                ("Name", info.Name),
                ("Version", (double)info.Version),
                ("Type", info.TypeName),
                ("Created (UTC)", Timestamp(info.CreatedUtc)),
                ("Description", info.Description),
            };
            foreach (var detail in info.Details) rows.Add((detail.Key, detail.Value));
            var result = new object[rows.Count, 2];
            for (int i = 0; i < rows.Count; i++)
            {
                result[i, 0] = rows[i].Item1;
                result[i, 1] = rows[i].Item2;
            }
            return result;
        });

        /// <summary>QF.OBJ.DELETE(handle) - TRUE if the object was removed, FALSE if the handle was unknown or stale.</summary>
        public object ObjDelete(object? handle) => Run("QF.OBJ.DELETE", () => (object)Cache.Delete(Arg.Text(handle, "handle")));

        private static string Timestamp(DateTime utc) => utc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }
}
