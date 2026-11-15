using System;
using System.Globalization;

namespace AsahiMono.Adapters
{
    /// <summary>
    /// <see cref="ISaveLocation"/> backed by Godot's <c>user://</c> filesystem.
    ///
    /// On every platform <c>user://</c> resolves to a per-application writable folder
    /// (on Windows: <c>%APPDATA%\Godot\app_userdata\AsahiMono</c>), which is exactly the
    /// sandboxed location iOS gives an app. Game.Core never sees this type: it only ever
    /// receives the text of a save.
    ///
    /// The full Godot type name is spelled out on purpose -- <c>System.IO.FileAccess</c> is
    /// a different thing and the two clash as soon as both namespaces are imported.
    /// </summary>
    public sealed class GodotSaveLocation : ISaveLocation
    {
        private readonly string _directory;

        /// <summary>Create a save location rooted at <paramref name="directory"/> ("user://" by default).</summary>
        /// <param name="directory">A Godot virtual path, e.g. <c>user://</c> or <c>user://saves</c>.</param>
        public GodotSaveLocation(string directory = "user://")
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new ArgumentException("directory must not be empty.", nameof(directory));
            }

            _directory = directory.EndsWith("/", StringComparison.Ordinal) ? directory : directory + "/";

            // Make sure the folder exists before anybody tries to write into it.
            // (DirAccess.MakeDirRecursiveAbsolute is a no-op when it already exists.)
            if (_directory != "user://")
            {
                Godot.DirAccess.MakeDirRecursiveAbsolute(_directory);
            }
        }

        /// <inheritdoc />
        public string DisplayName => _directory;

        /// <inheritdoc />
        public bool Exists(string fileName) => Godot.FileAccess.FileExists(Path(fileName));

        /// <inheritdoc />
        public string ReadAllText(string fileName)
        {
            string path = Path(fileName);
            using Godot.FileAccess file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read)
                ?? throw new InvalidOperationException(
                    string.Create(CultureInfo.InvariantCulture, $"Cannot read '{path}' (Godot error {Godot.FileAccess.GetOpenError()})."));

            return file.GetAsText();
        }

        /// <inheritdoc />
        public void WriteAllText(string fileName, string contents)
        {
            if (contents is null)
            {
                throw new ArgumentNullException(nameof(contents));
            }

            string path = Path(fileName);
            using Godot.FileAccess file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Write)
                ?? throw new InvalidOperationException(
                    string.Create(CultureInfo.InvariantCulture, $"Cannot write '{path}' (Godot error {Godot.FileAccess.GetOpenError()})."));

            file.StoreString(contents);
            file.Flush();
        }

        /// <inheritdoc />
        public bool Delete(string fileName)
        {
            string path = Path(fileName);
            if (!Godot.FileAccess.FileExists(path))
            {
                return false;
            }

            return Godot.DirAccess.RemoveAbsolute(path) == Godot.Error.Ok;
        }

        private string Path(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                throw new ArgumentException("fileName must not be empty.", nameof(fileName));
            }

            return _directory + fileName;
        }
    }
}
