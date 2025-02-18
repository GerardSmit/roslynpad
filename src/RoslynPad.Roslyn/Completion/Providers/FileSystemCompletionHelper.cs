// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Shared.Utilities;
using Roslyn.Utilities;

namespace RoslynPad.Roslyn;

internal class FileSystemCompletionHelper(
    Glyph folderGlyph,
    Glyph fileGlyph,
    ImmutableArray<string> searchPaths,
    string baseDirectoryOpt,
    ImmutableArray<string> allowableExtensions,
    CompletionItemRules itemRules)
{
    private static readonly char[] s_windowsDirectorySeparator = ['\\'];

    private readonly Glyph _folderGlyph = folderGlyph;
    private readonly Glyph _fileGlyph = fileGlyph;

    // absolute paths
    private readonly ImmutableArray<string> _searchPaths = searchPaths;
    private readonly string _baseDirectoryOpt = baseDirectoryOpt!;

    private readonly ImmutableArray<string> _allowableExtensions = allowableExtensions;
    private readonly CompletionItemRules _itemRules = itemRules;

    private string[] GetLogicalDrives()
        => IOUtilities.PerformIO(Directory.GetLogicalDrives, Array.Empty<string>());

    private bool DirectoryExists(string fullPath) =>
        Directory.Exists(fullPath);

    private IEnumerable<string> EnumerateDirectories(string fullDirectoryPath) =>
        IOUtilities.PerformIO(() => Directory.EnumerateDirectories(fullDirectoryPath), Array.Empty<string>());

    private IEnumerable<string> EnumerateFiles(string fullDirectoryPath) =>
        IOUtilities.PerformIO(() => Directory.EnumerateFiles(fullDirectoryPath), Array.Empty<string>());

    private bool IsVisibleFileSystemEntry(string fullPath) =>
        IOUtilities.PerformIO(() => (File.GetAttributes(fullPath) & (FileAttributes.Hidden | FileAttributes.System)) == 0, false);

    private CompletionItem CreateNetworkRoot()
        => CommonCompletionItem.Create(
            "\\\\",
            "",
            glyph: null,
            description: "\\\\".ToSymbolDisplayParts(),
            rules: _itemRules);

    private CompletionItem CreateUnixRoot()
        => CommonCompletionItem.Create(
            "/",
            "",
            glyph: _folderGlyph,
            description: "/".ToSymbolDisplayParts(),
            rules: _itemRules);

    private CompletionItem CreateFileSystemEntryItem(string fullPath, bool isDirectory)
        => CommonCompletionItem.Create(
            PathUtilities.GetFileName(fullPath),
            "",
            glyph: isDirectory ? _folderGlyph : _fileGlyph,
            description: fullPath.ToSymbolDisplayParts(),
            rules: _itemRules);

    private CompletionItem CreateLogicalDriveItem(string drive)
        => CommonCompletionItem.Create(
            drive,
            "",
            glyph: _folderGlyph,
            description: drive.ToSymbolDisplayParts(),
            rules: _itemRules);

    public Task<ImmutableArray<CompletionItem>> GetItemsAsync(string directoryPath, CancellationToken cancellationToken)
    {
        return Task.Run(() => GetItems(directoryPath, cancellationToken), cancellationToken);
    }

    private ImmutableArray<CompletionItem> GetItems(string directoryPath, CancellationToken cancellationToken)
    {
        if (!PathUtilities.IsUnixLikePlatform && directoryPath.Length == 1 && directoryPath[0] == '\\')
        {
            // The user has typed only "\".  In this case, we want to add "\\" to the list.  
            return [CreateNetworkRoot()];
        }

        var result = ArrayBuilder<CompletionItem>.GetInstance();

        var pathKind = PathUtilities.GetPathKind(directoryPath);
        switch (pathKind)
        {
            case PathKind.Empty:
                // base directory
                if (_baseDirectoryOpt != null)
                {
                    result.AddRange(GetItemsInDirectory(_baseDirectoryOpt, cancellationToken));
                }

                // roots
                if (PathUtilities.IsUnixLikePlatform)
                {
                    result.AddRange(CreateUnixRoot());
                }
                else
                {
                    foreach (var drive in GetLogicalDrives())
                    {
                        result.Add(CreateLogicalDriveItem(drive.TrimEnd(s_windowsDirectorySeparator)));
                    }

                    result.Add(CreateNetworkRoot());
                }

                // entries on search paths
                foreach (var searchPath in _searchPaths)
                {
                    result.AddRange(GetItemsInDirectory(searchPath, cancellationToken));
                }

                break;

            case PathKind.Absolute:
            case PathKind.RelativeToCurrentDirectory:
            case PathKind.RelativeToCurrentParent:
            case PathKind.RelativeToCurrentRoot:
                var fullDirectoryPath = FileUtilities.ResolveRelativePath(directoryPath, basePath: null, baseDirectory: _baseDirectoryOpt);
                if (fullDirectoryPath != null)
                {
                    result.AddRange(GetItemsInDirectory(fullDirectoryPath, cancellationToken));
                }
                else
                {
                    // invalid path
                    result.Clear();
                }

                break;

            case PathKind.Relative:

                // base directory:
                if (_baseDirectoryOpt != null)
                {
                    result.AddRange(GetItemsInDirectory(PathUtilities.CombineAbsoluteAndRelativePaths(_baseDirectoryOpt, directoryPath)!, cancellationToken));
                }

                // search paths:
                foreach (var searchPath in _searchPaths)
                {
                    result.AddRange(GetItemsInDirectory(PathUtilities.CombineAbsoluteAndRelativePaths(searchPath, directoryPath)!, cancellationToken));
                }

                break;

            case PathKind.RelativeToDriveDirectory:
                // Paths "C:dir" are not supported, but when the path doesn't include any directory, i.e. "C:",
                // we return the drive itself.
                if (directoryPath.Length == 2)
                {
                    result.Add(CreateLogicalDriveItem(directoryPath));
                }

                break;

            default:
                throw ExceptionUtilities.UnexpectedValue(pathKind);
        }

        return result.ToImmutableAndFree();
    }

    private IEnumerable<CompletionItem> GetItemsInDirectory(string fullDirectoryPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!DirectoryExists(fullDirectoryPath))
        {
            yield break;
        }

        cancellationToken.ThrowIfCancellationRequested();

        foreach (var directory in EnumerateDirectories(fullDirectoryPath))
        {
            if (IsVisibleFileSystemEntry(directory))
            {
                yield return CreateFileSystemEntryItem(directory, isDirectory: true);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        foreach (var file in EnumerateFiles(fullDirectoryPath))
        {
            if (_allowableExtensions.Length != 0 &&
                !_allowableExtensions.Contains(
                    PathUtilities.GetExtension(file),
                    PathUtilities.IsUnixLikePlatform ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (IsVisibleFileSystemEntry(file))
            {
                yield return CreateFileSystemEntryItem(file, isDirectory: false);
            }
        }
    }
}
