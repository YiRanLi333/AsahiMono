namespace AsahiMono.Adapters
{
    /// <summary>
    /// Adapter-layer port for save files.
    ///
    /// Game.Core is not allowed to touch the file system, so anything that needs to
    /// read or write a save goes through an implementation of this interface
    /// (Godot user:// on device, a plain folder on desktop, memory in tests).
    /// Placeholder for M0: the real save format arrives in M4.
    /// </summary>
    public interface ISaveLocation
    {
        /// <summary>Human readable location, for logs only (never parsed).</summary>
        string DisplayName { get; }

        /// <summary>True when a save with this logical file name already exists.</summary>
        bool Exists(string fileName);

        /// <summary>Read a save file's full text. Throws when it does not exist.</summary>
        string ReadAllText(string fileName);

        /// <summary>Write a save file's full text, replacing any previous content.</summary>
        void WriteAllText(string fileName, string contents);

        /// <summary>Delete a save file if present. Returns true when something was deleted.</summary>
        bool Delete(string fileName);
    }
}
