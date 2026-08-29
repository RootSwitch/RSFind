// Persisted preferences: %APPDATA%\RSFind\settings.ini
//
// Plain key=value rather than JSON, for the same reason as RSPaster: there is
// no in-box JSON parser that does not pull in another assembly reference, and
// a hand-rolled one for a dozen scalar fields is risk with no return. The file
// is also something a person can read and edit.
//
// What is NOT stored: the search query, ever, and no history of past ones.
// A query typed against a folder of console logs is as likely to be a serial
// number, a hostname, or a password as it is to be "smartctl", and a search
// tool that quietly keeps a list of everything you looked for is a tool you
// have to think about before using. Not writing them is cheaper than
// explaining them. The folder path is stored, because it is the one field
// whose loss makes the tool annoying to reopen and it is already visible in
// the window title.
//
// C# 5 only (in-box csc).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace RSFind
{
    public class Settings
    {
        public string Theme = "classic";

        public string LastFolder = "";
        public string IncludeMasks = "";
        public string ExcludeMasks = "";

        public bool MatchCase;
        public bool WholeWord;
        public bool UseRegex;

        public bool IncludeSubfolders = true;
        public bool ExcludeBinary = true;

        // On by default, which is the one place the app disagrees with the
        // engine. SearchOptions defaults it off because a library should hand
        // back what is on disk; the app defaults it on because the folder it
        // was built for is full of raw terminal logs, where leaving it off
        // means every prompt line renders as a row of boxes and a phrase that
        // straddles a color code cannot be found at all. It is one labeled
        // checkbox away either direction.
        public bool StripAnsi = true;

        public int MaxFileMegabytes = 50;
        public int ContextBefore;
        public int ContextAfter;

        // {file} and {line} are substituted. Blank means the shell association,
        // which is what most people want and what works with no configuration.
        // The same $EDITOR-shaped thinking as RSMultiTerm's Edit Locally.
        public string EditorCommand = "";

        public int WindowWidth;
        public int WindowHeight;
        public bool WindowMaximized;

        public static string Dir
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "RSFind");
            }
        }

        public static string FilePath { get { return Path.Combine(Dir, "settings.ini"); } }

        public static Settings Load()
        {
            Settings s = new Settings();
            Dictionary<string, string> kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(FilePath)) return s;
                foreach (string raw in File.ReadAllLines(FilePath))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    kv[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
            }
            catch (IOException) { return s; }
            catch (UnauthorizedAccessException) { return s; }

            s.Theme = Str(kv, "theme", s.Theme);
            s.LastFolder = Str(kv, "lastFolder", s.LastFolder);
            s.IncludeMasks = Str(kv, "includeMasks", s.IncludeMasks);
            s.ExcludeMasks = Str(kv, "excludeMasks", s.ExcludeMasks);
            s.MatchCase = Bool(kv, "matchCase", s.MatchCase);
            s.WholeWord = Bool(kv, "wholeWord", s.WholeWord);
            s.UseRegex = Bool(kv, "useRegex", s.UseRegex);
            s.IncludeSubfolders = Bool(kv, "includeSubfolders", s.IncludeSubfolders);
            s.ExcludeBinary = Bool(kv, "excludeBinary", s.ExcludeBinary);
            s.StripAnsi = Bool(kv, "stripAnsi", s.StripAnsi);
            s.MaxFileMegabytes = Int(kv, "maxFileMegabytes", s.MaxFileMegabytes, 0, 4096);
            s.ContextBefore = Int(kv, "contextBefore", s.ContextBefore, 0, 20);
            s.ContextAfter = Int(kv, "contextAfter", s.ContextAfter, 0, 20);
            s.EditorCommand = Str(kv, "editorCommand", s.EditorCommand);
            s.WindowWidth = Int(kv, "windowWidth", 0, 0, 20000);
            s.WindowHeight = Int(kv, "windowHeight", 0, 0, 20000);
            s.WindowMaximized = Bool(kv, "windowMaximized", s.WindowMaximized);
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# RSFind settings. Search queries are never stored here.");
                Write(sb, "theme", Theme);
                Write(sb, "lastFolder", LastFolder);
                Write(sb, "includeMasks", IncludeMasks);
                Write(sb, "excludeMasks", ExcludeMasks);
                Write(sb, "matchCase", MatchCase ? "true" : "false");
                Write(sb, "wholeWord", WholeWord ? "true" : "false");
                Write(sb, "useRegex", UseRegex ? "true" : "false");
                Write(sb, "includeSubfolders", IncludeSubfolders ? "true" : "false");
                Write(sb, "excludeBinary", ExcludeBinary ? "true" : "false");
                Write(sb, "stripAnsi", StripAnsi ? "true" : "false");
                Write(sb, "maxFileMegabytes", MaxFileMegabytes.ToString(CultureInfo.InvariantCulture));
                Write(sb, "contextBefore", ContextBefore.ToString(CultureInfo.InvariantCulture));
                Write(sb, "contextAfter", ContextAfter.ToString(CultureInfo.InvariantCulture));
                Write(sb, "editorCommand", EditorCommand);
                Write(sb, "windowWidth", WindowWidth.ToString(CultureInfo.InvariantCulture));
                Write(sb, "windowHeight", WindowHeight.ToString(CultureInfo.InvariantCulture));
                Write(sb, "windowMaximized", WindowMaximized ? "true" : "false");
                File.WriteAllText(FilePath, sb.ToString());
            }
            catch (IOException) { /* a settings file we cannot write is not worth a dialog */ }
            catch (UnauthorizedAccessException) { }
        }

        static void Write(StringBuilder sb, string key, string value)
        {
            // A value carrying a line break would forge the line after it, and
            // the next load would parse the forgery as a setting. A pasted
            // folder path or editor command is the plausible route in.
            sb.Append(key).Append('=')
              .AppendLine((value == null ? "" : value).Replace('\r', ' ').Replace('\n', ' ').Trim());
        }

        static string Str(Dictionary<string, string> kv, string key, string fallback)
        {
            string v;
            return kv.TryGetValue(key, out v) && v.Length > 0 ? v : fallback;
        }

        static int Int(Dictionary<string, string> kv, string key, int fallback, int min, int max)
        {
            string v;
            int n;
            if (!kv.TryGetValue(key, out v)) return fallback;
            if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return fallback;
            if (n < min) return min;
            if (n > max) return max;
            return n;
        }

        static bool Bool(Dictionary<string, string> kv, string key, bool fallback)
        {
            string v;
            if (!kv.TryGetValue(key, out v)) return fallback;
            return v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1";
        }
    }
}
