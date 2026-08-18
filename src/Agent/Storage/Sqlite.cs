using System;
using System.Runtime.InteropServices;
using System.Text;

namespace AgentOffice.Agent.Storage
{
/// <summary>Minimal SQLite wrapper over winsqlite3.dll — the SQLite build inbox in Windows 10 (1809+) / 11.</summary>
    internal static class SqliteNative
    {
        private const string Dll = "winsqlite3.dll";

        internal const int SQLITE_OK = 0;
        internal const int SQLITE_ROW = 100;
        internal const int SQLITE_DONE = 101;
        internal const int SQLITE_NULL = 5;

        // READWRITE | CREATE | FULLMUTEX (serialized — safe from any thread).
        internal const int OPEN_FLAGS = 0x2 | 0x4 | 0x100;

/// <summary>Sqlite3_bind_text destructor that makes SQLite copy the value.</summary>
        internal static readonly IntPtr TRANSIENT = new IntPtr(-1);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_close(IntPtr db);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_exec(IntPtr db, byte[] sql, IntPtr callback, IntPtr arg, out IntPtr errmsg);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int nbytes, out IntPtr stmt, IntPtr tail);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_step(IntPtr stmt);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_finalize(IntPtr stmt);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_bind_text(IntPtr stmt, int index, byte[] value, int nbytes, IntPtr destructor);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_bind_int64(IntPtr stmt, int index, long value);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_bind_double(IntPtr stmt, int index, double value);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_bind_null(IntPtr stmt, int index);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr sqlite3_column_text(IntPtr stmt, int index);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern long sqlite3_column_int64(IntPtr stmt, int index);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern double sqlite3_column_double(IntPtr stmt, int index);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_column_bytes(IntPtr stmt, int index);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_column_type(IntPtr stmt, int index);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr sqlite3_errmsg(IntPtr db);

        internal static byte[] Utf8(string value)
        {
            return Encoding.UTF8.GetBytes(value + "\0");
        }

/// <summary>Read a null-terminated UTF-8 pointer (errmsg, column_text).</summary>
        internal static string PtrToStringUtf8(IntPtr ptr)
        {
            if (ptr == IntPtr.Zero) return null;
            var length = 0;
            while (Marshal.ReadByte(ptr, length) != 0) length++;
            if (length == 0) return string.Empty;
            var buffer = new byte[length];
            Marshal.Copy(ptr, buffer, 0, length);
            return Encoding.UTF8.GetString(buffer);
        }
    }

    internal sealed class SqliteException : Exception
    {
        public SqliteException(string message) : base(message) { }
    }

/// <summary>An open SQLite database.</summary>
    internal sealed class SqliteDb : IDisposable
    {
        private IntPtr _db;

        private SqliteDb(IntPtr db) { _db = db; }

        public static SqliteDb Open(string path)
        {
            IntPtr db;
            var rc = SqliteNative.sqlite3_open_v2(
                SqliteNative.Utf8(path), out db, SqliteNative.OPEN_FLAGS, IntPtr.Zero);
            if (rc != SqliteNative.SQLITE_OK)
            {
                var message = db != IntPtr.Zero
                    ? SqliteNative.PtrToStringUtf8(SqliteNative.sqlite3_errmsg(db))
                    : "open failed (rc " + rc + ")";
                if (db != IntPtr.Zero) SqliteNative.sqlite3_close(db);
                throw new SqliteException("sqlite open failed: " + message);
            }
            return new SqliteDb(db);
        }

/// <summary>Run statements with no results (PRAGMA, DDL, etc.).</summary>
        public void Exec(string sql)
        {
            IntPtr error;
            var rc = SqliteNative.sqlite3_exec(
                _db, SqliteNative.Utf8(sql), IntPtr.Zero, IntPtr.Zero, out error);
            if (rc != SqliteNative.SQLITE_OK)
            {
                var message = SqliteNative.PtrToStringUtf8(error);
                throw new SqliteException("sqlite exec failed (" + rc + "): " + message);
            }
        }

        public SqliteStmt Prepare(string sql)
        {
            IntPtr stmt;
            var bytes = SqliteNative.Utf8(sql);
            var rc = SqliteNative.sqlite3_prepare_v2(_db, bytes, bytes.Length, out stmt, IntPtr.Zero);
            if (rc != SqliteNative.SQLITE_OK)
            {
                throw new SqliteException("sqlite prepare failed (" + rc + "): " +
                    SqliteNative.PtrToStringUtf8(SqliteNative.sqlite3_errmsg(_db)) +
                    " — sql: " + sql);
            }
            return new SqliteStmt(stmt);
        }

        public void Dispose()
        {
            if (_db != IntPtr.Zero)
            {
                SqliteNative.sqlite3_close(_db);
                _db = IntPtr.Zero;
            }
        }
    }

/// <summary>A prepared statement.</summary>
    internal sealed class SqliteStmt : IDisposable
    {
        private IntPtr _stmt;

        internal SqliteStmt(IntPtr stmt) { _stmt = stmt; }

        public SqliteStmt Bind(int index, string value)
        {
            if (value == null) return BindNull(index);
            var bytes = Encoding.UTF8.GetBytes(value);
            SqliteNative.sqlite3_bind_text(_stmt, index, bytes, bytes.Length, SqliteNative.TRANSIENT);
            return this;
        }

        public SqliteStmt Bind(int index, long value)
        {
            SqliteNative.sqlite3_bind_int64(_stmt, index, value);
            return this;
        }

        public SqliteStmt Bind(int index, double value)
        {
            SqliteNative.sqlite3_bind_double(_stmt, index, value);
            return this;
        }

        public SqliteStmt BindNull(int index)
        {
            SqliteNative.sqlite3_bind_null(_stmt, index);
            return this;
        }

/// <summary>Advance; true when a row is available, false at DONE.</summary>
        public bool Step()
        {
            var rc = SqliteNative.sqlite3_step(_stmt);
            if (rc == SqliteNative.SQLITE_ROW) return true;
            if (rc == SqliteNative.SQLITE_DONE) return false;
            throw new SqliteException("sqlite step failed (rc " + rc + ")");
        }

        public string ColText(int index)
        {
            if (SqliteNative.sqlite3_column_type(_stmt, index) == SqliteNative.SQLITE_NULL)
                return null;
            var ptr = SqliteNative.sqlite3_column_text(_stmt, index);
            var length = SqliteNative.sqlite3_column_bytes(_stmt, index);
            if (ptr == IntPtr.Zero || length <= 0) return string.Empty;
            var buffer = new byte[length];
            Marshal.Copy(ptr, buffer, 0, length);
            return Encoding.UTF8.GetString(buffer);
        }

        public long ColInt64(int index)
        {
            return SqliteNative.sqlite3_column_int64(_stmt, index);
        }

        public double ColDouble(int index)
        {
            return SqliteNative.sqlite3_column_double(_stmt, index);
        }

        public void Dispose()
        {
            if (_stmt != IntPtr.Zero)
            {
                SqliteNative.sqlite3_finalize(_stmt);
                _stmt = IntPtr.Zero;
            }
        }
    }
}
