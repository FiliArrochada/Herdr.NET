using HerdrNet.Model;
using HerdrNet.Serialization;

namespace HerdrNet;

/// <summary>
/// Files on the core's computer, or on a machine added to it (<c>on</c>). A relative path starts at
/// <c>cwd</c>, else in the folder of whatever connected to the core (on a machine: its home); the core
/// expands no <c>~</c>.
/// </summary>
public sealed class FilesClient
{
    static HerdrJsonContext Json => HerdrJsonContext.Default;

    readonly HerdrConnection _connection;

    internal FilesClient(HerdrConnection connection) => _connection = connection;

    /// <summary>Reads a text file: lines from <paramref name="offset"/> (from 1), at most <paramref name="limit"/> and 8 MB.</summary>
    public Task<FileReadResult> ReadAsync(string path, int? offset = null, int? limit = null, string? cwd = null, string? on = null, CancellationToken cancellationToken = default) =>
        _connection.CallAsync(HerdrMethods.FileRead, new FileReadParams(path, cwd, offset, limit, on), Json.FileReadParams, Json.FileReadResult, cancellationToken);

    /// <summary>
    /// Writes a file whole (at most 8 MB), making missing folders: a reader sees the old or the new, never
    /// half. An existing file keeps its mode; a symlink is followed.
    /// </summary>
    public Task<FileResult> WriteAsync(string path, byte[] data, string? cwd = null, string? on = null, CancellationToken cancellationToken = default) =>
        _connection.CallAsync(HerdrMethods.FileWrite, new FileWriteParams(path, data, cwd, on), Json.FileWriteParams, Json.FileResult, cancellationToken);

    /// <summary>Replaces text in a file: each edit's old text found exactly once, none overlapping; all are made or none.</summary>
    public Task<FileResult> EditAsync(string path, IReadOnlyList<FileEdit> edits, string? cwd = null, string? on = null, CancellationToken cancellationToken = default) =>
        _connection.CallAsync(HerdrMethods.FileEdit, new FileEditParams(path, edits, cwd, on), Json.FileEditParams, Json.FileResult, cancellationToken);
}
