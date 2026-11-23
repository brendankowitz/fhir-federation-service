using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;

namespace Ignixa.Federation.Shards.Credentials;

/// <summary>
/// Supplies shard client secrets from complete files in one operator-provisioned local directory, for example a secret
/// store's read-only volume mount (design spec §10.2). It reopens the mapped file for every lookup, so a rotated file is
/// served by the next lookup, and it never caches a value, not even to bridge a failure.
/// </summary>
/// <remarks>
/// <para><b>Operator contract.</b> The root is a dedicated, local, read-only directory that only the operator can change:
/// the operator controls the root, its ancestors, the name map and every leaf. Each leaf is a regular file holding exactly
/// one secret. A symbolic link or other reparse point, a directory, a special file (device, pipe, socket), a network or
/// pseudo file system and a mount of a single file are all outside the contract. The resolver refuses a leaf that
/// reports itself as a directory or reparse point, before it opens it. That check is a best-effort guard against operator
/// mistakes. It is <b>not</b> a race-safe provenance guarantee: the file is opened afterwards by path, the platform's open
/// follows whatever is there at that moment, hard links and a directory that a writer can change are undetectable, and no
/// claim is made that an arbitrary special file can be safely inspected. Do not point this at a directory an attacker can
/// write. A secret-store volume that publishes leaves as links (some orchestrators do) must be projected into the dedicated
/// directory as regular files first.</para>
/// <para><b>Rotation.</b> Publish a new value by writing a complete sibling file in the same directory and replacing the leaf
/// with it in one atomic operation: <c>rename</c> on Unix, or <see cref="File.Replace(string, string, string?)"/> on Windows,
/// because a plain overwriting move is refused there while a lookup has the file open. Then verify the new value is
/// visible in the mount. A lookup opens the file with delete sharing, so a replace is not blocked by it, and a lookup sees
/// either the whole old file, the whole new file, or fails closed. Do not rewrite a leaf in place.</para>
/// <para><b>Names.</b> The logical names are opaque keys of the map given to the constructor, compared ordinally. A name
/// that is not in the map is unavailable and never touches the file system, whatever it looks like. A leaf is one plain file
/// name of ASCII letters, digits, <c>.</c>, <c>-</c> and <c>_</c>, at most 255 characters, that is not a Windows device
/// name, does not end in a dot and is not made only of dots. Because every caller reaches the same directory, a name is a
/// lookup key, not an authorization boundary between shards. The host that composes this validates that every shard secret
/// name is mapped, without reading values.</para>
/// <para><b>Values.</b> A value is the whole file as strict UTF-8, returned exactly as stored, including leading and trailing
/// spaces. At most 4,096 bytes are accepted; the resolver reads no more than 4,097 bytes. A byte order mark, invalid UTF-8,
/// NUL, CR or LF anywhere (so a trailing newline too), or a value that is empty or only white space, is refused.</para>
/// <para><b>Failures.</b> An unmapped name, a missing, unreadable or malformed leaf, and a value over the limit all throw a
/// fresh <see cref="ShardCredentialException"/> with <see cref="ShardCredentialFailure.SecretUnavailable"/>, no inner
/// exception and no diagnostics from the file system or decoder. Constructor errors never quote the rejected input.
/// Cancellation is checked before open, during reads, and before returning a value or reporting an expected failure.
/// It does not impose a deadline on the platform's synchronous open. The scratch bytes are cleared after each read;
/// the returned string cannot be. This resolver is safe for concurrent calls.</para>
/// </remarks>
public sealed class MountedShardSecretResolver : IShardSecretResolver
{
    private const int MaxValueBytes = 4096;
    private const int MaxLeafLength = 255;

    // Strict decoding: invalid bytes throw instead of becoming U+FFFD. A BOM is not consumed, so it is checked on the text.
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly string[] ReservedDeviceNames = ["CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "CLOCK$"];

    private readonly string _root;
    private readonly FrozenDictionary<string, string> _leaves;

    /// <summary>Creates the resolver over a mounted directory and a fixed map of logical names to leaf file names.</summary>
    /// <param name="rootDirectory">The fully qualified local directory the operator mounted. It is normalized but not read.</param>
    /// <param name="files">The logical names (opaque, ordinal) and the file name each one reads inside the root. Snapshotted.</param>
    /// <exception cref="ArgumentNullException"><paramref name="rootDirectory"/> or <paramref name="files"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The root is not a usable fully qualified local path, or an entry has a blank name, an unsafe leaf or a duplicate name.
    /// The message never quotes the rejected value.
    /// </exception>
    public MountedShardSecretResolver(string rootDirectory, IEnumerable<KeyValuePair<string, string>> files)
    {
        ArgumentNullException.ThrowIfNull(rootDirectory);
        ArgumentNullException.ThrowIfNull(files);
        _root = NormalizeRoot(rootDirectory);

        var leaves = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string? name, string? leaf) in files)
        {
            if (string.IsNullOrWhiteSpace(name) || leaf is null || !IsSafeLeaf(leaf))
            {
                throw new ArgumentException("Each file entry needs a non-blank name and a plain, safe file name.", nameof(files));
            }

            if (!leaves.TryAdd(name, leaf))
            {
                throw new ArgumentException("Each secret name may be mapped only once.", nameof(files));
            }
        }

        _leaves = leaves.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="secretName"/> is <see langword="null"/>.</exception>
    /// <exception cref="ShardCredentialException">
    /// The name is unmapped, or the leaf is missing, unreadable, oversize or malformed; the message is fixed and carries no detail.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public async ValueTask<string> ResolveAsync(string secretName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(secretName);
        if (!_leaves.TryGetValue(secretName, out string? leaf))
        {
            throw Unavailable(cancellationToken);
        }

        string? value;
        try
        {
            value = await ReadAsync(Path.Combine(_root, leaf), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or DecoderFallbackException)
        {
            value = null;
        }

        // A value read after the caller gave up is discarded, and cancellation outranks the failure conversion.
        cancellationToken.ThrowIfCancellationRequested();
        return value ?? throw Unavailable(cancellationToken);
    }

    private static async ValueTask<string?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        FileAttributes attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.Directory) || attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return null;
        }

        var options = new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read | FileShare.Delete,
            Options = FileOptions.Asynchronous,
            BufferSize = 0,
        };

        byte[] scratch = new byte[MaxValueBytes + 1];
        try
        {
            int length = 0;
            cancellationToken.ThrowIfCancellationRequested();
            using (var stream = new FileStream(path, options))
            {
                int read;
                while (length < scratch.Length
                    && (read = await stream.ReadAsync(scratch.AsMemory(length), cancellationToken).ConfigureAwait(false)) > 0)
                {
                    length += read;
                }
            }

            return length is 0 or > MaxValueBytes ? null : ValidValue(StrictUtf8.GetString(scratch, 0, length));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(scratch);
        }
    }

    private static string? ValidValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value[0] == '\uFEFF' || value.AsSpan().IndexOfAny('\0', '\r', '\n') >= 0)
        {
            return null;
        }

        return value;
    }

    // Cancellation outranks the expected-failure conversion, so the caller's token is checked at the point of conversion.
    private static ShardCredentialException Unavailable(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ShardCredentialException(ShardCredentialFailure.SecretUnavailable);
    }

    private static string NormalizeRoot(string root)
    {
        // Windows UNC and device forms (\\server\share, \\?\, \\.\) are network or device roots, not a local directory.
        bool networkOrDevice = OperatingSystem.IsWindows() && root.Length >= 2 && IsSeparator(root[0]) && IsSeparator(root[1]);
        if (networkOrDevice || !Path.IsPathFullyQualified(root) || root.Any(char.IsControl))
        {
            throw RootRefused();
        }

        try
        {
            return Path.GetFullPath(root);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            throw RootRefused();
        }
    }

    private static ArgumentException RootRefused() =>
        new("The mount root must be a fully qualified local directory path.", "rootDirectory");

    private static bool IsSeparator(char c) => c is '/' or '\\';

    // An allow-list: it rules out separators, rooted and drive forms, stream syntax, wildcards, control characters,
    // trailing-space aliases, 8.3 short names (~) and non-ASCII look-alikes in one rule.
    private static bool IsSafeLeaf(string leaf)
    {
        if (leaf.Length is 0 or > MaxLeafLength || leaf[^1] == '.')
        {
            return false;
        }

        foreach (char c in leaf)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))
            {
                return false;
            }
        }

        return !IsDeviceName(leaf);
    }

    // A Windows device name is reserved with or without an extension: CON, con.txt, COM1.log, LPT9.
    private static bool IsDeviceName(string leaf)
    {
        int dot = leaf.IndexOf('.', StringComparison.Ordinal);
        ReadOnlySpan<char> stem = dot < 0 ? leaf : leaf.AsSpan(0, dot);
        foreach (string reserved in ReservedDeviceNames)
        {
            if (stem.Equals(reserved, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return stem.Length == 4
            && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
            && stem[3] is >= '1' and <= '9';
    }
}
