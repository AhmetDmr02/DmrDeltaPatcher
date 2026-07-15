using DeltaQ.BsDiff;
using DeltaQ.SuffixSorting;
using DeltaQ.SuffixSorting.LibDivSufSort;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

public static class Program
{
    private const long LargeFileThreshold = 50 * 1024 * 1024; // 50MB

    public static void Main(string[] args)
    {
        string patchZoneDir = Path.Combine(AppContext.BaseDirectory, "PatchZone");
        string outPatchDir = Path.Combine(AppContext.BaseDirectory, "OutPatch");

        if (!Directory.Exists(patchZoneDir)) Directory.CreateDirectory(patchZoneDir);
        if (!Directory.Exists(outPatchDir)) Directory.CreateDirectory(outPatchDir);

        while (true)
        {
            Console.WriteLine("== DMR PATCH CREATOR ==");
            Console.WriteLine("1. Create Patch (Supports .zip OR raw directory paths)");
            Console.WriteLine("2. Generate Baseline Hash Copy");
            Console.WriteLine("3. Exit");
            Console.Write("\nSelect Mode: ");

            string choice = (Console.ReadLine() ?? "").Trim();
            if (choice == "3") break;

            if (choice == "1")
            {
                Console.WriteLine("Enter Baseline Name (e.g., baseline.zip OR folder name):");
                string baseLineInput = Path.Combine(patchZoneDir, (Console.ReadLine() ?? "").Trim());

                Console.WriteLine("Enter Updated Name (e.g., updated.zip OR folder name):");
                string updatedInput = Path.Combine(patchZoneDir, (Console.ReadLine() ?? "").Trim());

                if ((!File.Exists(baseLineInput) && !Directory.Exists(baseLineInput)) ||
                    (!File.Exists(updatedInput) && !Directory.Exists(updatedInput)))
                {
                    Console.WriteLine("Inputs missing! Make sure they exist in PatchZone. Press any key...");
                    Console.ReadKey();
                    continue;
                }

                CreateSmartPatch(baseLineInput, updatedInput);
                Console.WriteLine("Press any key to continue...");
                Console.ReadKey();
            }
            else if (choice == "2")
            {
                Console.WriteLine("Enter Baseline File Name (must be inside PatchZone):");
                string baseLinePath = Path.Combine(patchZoneDir, (Console.ReadLine() ?? "").Trim());
                if (File.Exists(baseLinePath))
                {
                    CreateHashNamedBaseline(baseLinePath);
                    Console.WriteLine("Press any key to continue...");
                    Console.ReadKey();
                }
                else
                {
                    Console.WriteLine("File not found! Press any key...");
                    Console.ReadKey();
                }
            }
        }
    }

    public static void CreateSmartPatch(string baselineInput, string updatedInput)
    {
        string tempDir = Path.Combine(AppContext.BaseDirectory, "TempProcessing");
        string patchBuildDir = Path.Combine(tempDir, "PatchContainer");

        if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(patchBuildDir);

        bool oldIsDir = Directory.Exists(baselineInput);
        bool newIsDir = Directory.Exists(updatedInput);

        string oldExtract = oldIsDir ? baselineInput : Path.Combine(tempDir, "Old");
        string newExtract = newIsDir ? updatedInput : Path.Combine(tempDir, "New");

        Console.WriteLine("Preparing inputs for file-by-file comparison...");
        if (!oldIsDir) { Directory.CreateDirectory(oldExtract); ZipFile.ExtractToDirectory(baselineInput, oldExtract); }
        if (!newIsDir) { Directory.CreateDirectory(newExtract); ZipFile.ExtractToDirectory(updatedInput, newExtract); }

        List<string> deletions = new List<string>();
        var oldFiles = Directory.GetFiles(oldExtract, "*.*", SearchOption.AllDirectories);
        var newFiles = Directory.GetFiles(newExtract, "*.*", SearchOption.AllDirectories);

        var oldRelativePaths = oldFiles.Select(f => Path.GetRelativePath(oldExtract, f)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newRelativePaths = newFiles.Select(f => Path.GetRelativePath(newExtract, f)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var suffixSorter = new LibDivSufSort();

        foreach (string newRelPath in newRelativePaths)
        {
            string newFilePath = Path.Combine(newExtract, newRelPath);
            string oldFilePath = Path.Combine(oldExtract, newRelPath);
            string patchDestPath = Path.Combine(patchBuildDir, newRelPath);

            Directory.CreateDirectory(Path.GetDirectoryName(patchDestPath)!);

            if (oldRelativePaths.Contains(newRelPath))
            {
                if (GetFileSHA256(oldFilePath) != GetFileSHA256(newFilePath))
                {
                    long oldSize = new FileInfo(oldFilePath).Length;
                    long newSize = new FileInfo(newFilePath).Length;

                    if (oldSize > LargeFileThreshold || newSize > LargeFileThreshold)
                    {
                        Console.WriteLine($"[LARGE FILE] Applying CDC mapping to {newRelPath}...");
                        ProcessLargeFileDelta(oldFilePath, newFilePath, patchDestPath, patchBuildDir, tempDir);
                    }
                    else
                    {
                        Console.WriteLine($"[MODIFIED] Diffing {newRelPath}...");
                        using (var outStream = File.Create(patchDestPath + ".delta"))
                        {
                            DeltaQ.BsDiff.Diff.Create(File.ReadAllBytes(oldFilePath), File.ReadAllBytes(newFilePath), outStream, suffixSorter);
                        }
                    }
                }
            }
            else
            {
                Console.WriteLine($"[ADDED] {newRelPath}");
                File.Copy(newFilePath, patchDestPath, true);
            }
        }

        foreach (string oldRelPath in oldRelativePaths)
        {
            if (!newRelativePaths.Contains(oldRelPath))
            {
                Console.WriteLine($"[DELETED] {oldRelPath}");
                deletions.Add(oldRelPath);
            }
        }

        if (deletions.Count > 0) File.WriteAllLines(Path.Combine(patchBuildDir, "dmr_deletions.txt"), deletions);

        Console.WriteLine("\nPackaging deterministic smart patch container...");
        string tempPatchZip = Path.Combine(tempDir, "temp_patch.zip");
        if (File.Exists(tempPatchZip)) File.Delete(tempPatchZip);

        // Deterministic packaging to prevent OS timestamps from altering the final hash
        using (var zipStream = new FileStream(tempPatchZip, FileMode.Create))
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
        {
            var allFiles = Directory.GetFiles(patchBuildDir, "*.*", SearchOption.AllDirectories)
                                    .OrderBy(f => f, StringComparer.Ordinal)
                                    .ToList();

            DateTime frozenTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            foreach (string file in allFiles)
            {
                string relPath = Path.GetRelativePath(patchBuildDir, file).Replace("\\", "/");
                var entry = archive.CreateEntry(relPath, CompressionLevel.Optimal);
                entry.LastWriteTime = frozenTime;

                using (var entryStream = entry.Open())
                using (var fs = File.OpenRead(file))
                {
                    fs.CopyTo(entryStream);
                }
            }
        }

        string patchHash = GetFileSHA256(tempPatchZip);
        string finalPatchPath = Path.Combine(AppContext.BaseDirectory, "OutPatch", patchHash + ".patch");

        if (File.Exists(finalPatchPath)) File.Delete(finalPatchPath);
        File.Move(tempPatchZip, finalPatchPath);

        if (!oldIsDir && Directory.Exists(oldExtract)) Directory.Delete(oldExtract, true);
        if (!newIsDir && Directory.Exists(newExtract)) Directory.Delete(newExtract, true);
        Directory.Delete(tempDir, true);

        Console.WriteLine($"\nSmart Patch generated completely: {patchHash}.patch");
    }

    private static void ProcessLargeFileDelta(string oldFilePath, string newFilePath, string patchDestPath, string patchBuildDir, string tempDir)
    {
        string chunksOldDir = Path.Combine(tempDir, "ChunksOld");
        string chunksNewDir = Path.Combine(tempDir, "ChunksNew");

        var oldChunks = ContentDefinedChunker.ChunkFile(oldFilePath, chunksOldDir);
        var newChunks = ContentDefinedChunker.ChunkFile(newFilePath, chunksNewDir);

        var oldChunksByHash = oldChunks.GroupBy(c => c.Sha256).ToDictionary(g => g.Key, g => g.First());

        var results = new ConcurrentBag<(int Index, LargeFileRecipeStep Step)>();

        int totalChunks = newChunks.Count;
        int completedChunks = 0;

        // Path of this file relative to the patch container root (e.g. "Data/textures/big.bin"),
        // used so the recipe can locate chunk files regardless of subdirectory.
        string relPathInPatch = Path.GetRelativePath(patchBuildDir, patchDestPath).Replace("\\", "/");

        Console.WriteLine($"Starting parallel delta generation for {Path.GetFileName(newFilePath)}...");

        Parallel.ForEach(newChunks, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, (newChunk) =>
        {
            var localSorter = new LibDivSufSort();
            int index = newChunk.Index;

            if (oldChunksByHash.TryGetValue(newChunk.Sha256, out var matchedOldChunk))
            {
                results.Add((index, new LargeFileRecipeStep
                {
                    Action = "Keep",
                    SourceOffset = matchedOldChunk.Offset,
                    SourceLength = matchedOldChunk.Length
                }));
            }
            else if (index < oldChunks.Count)
            {
                var oldChunkToDiff = oldChunks[index];
                byte[] oldBytes = File.ReadAllBytes(Path.Combine(chunksOldDir, $"{oldChunkToDiff.Sha256}.chunk"));
                byte[] newBytes = File.ReadAllBytes(Path.Combine(chunksNewDir, $"{newChunk.Sha256}.chunk"));

                // Store the path RELATIVE TO THE PATCH ROOT, not just the bare filename,
                // so the applier can find it even when the file lives in a subdirectory.
                string deltaFileName = relPathInPatch + $".chunk_{index}.delta";
                string deltaPath = patchDestPath + $".chunk_{index}.delta";

                using (var outStream = File.Create(deltaPath))
                {
                    DeltaQ.BsDiff.Diff.Create(oldBytes, newBytes, outStream, localSorter);
                }

                results.Add((index, new LargeFileRecipeStep
                {
                    Action = "Patch",
                    SourceOffset = oldChunkToDiff.Offset,
                    SourceLength = oldChunkToDiff.Length,
                    DeltaFileName = deltaFileName
                }));
            }
            else
            {
                string rawName = relPathInPatch + $".chunk_{index}.raw";
                File.Copy(Path.Combine(chunksNewDir, $"{newChunk.Sha256}.chunk"), patchDestPath + $".chunk_{index}.raw", true);

                results.Add((index, new LargeFileRecipeStep { Action = "New", DeltaFileName = rawName }));
            }

            int currentCount = Interlocked.Increment(ref completedChunks);
            lock (Console.Out)
            {
                double percent = (double)currentCount / totalChunks * 100;
                Console.Write($"\r[PROGRESS] {Path.GetFileName(newFilePath)}: {percent:F1}% ({currentCount}/{totalChunks})");
            }
        });

        Console.WriteLine("\nDelta generation complete. Saving recipe...");

        var largeFileRecipe = new LargeFileRecipe
        {
            Steps = results.OrderBy(r => r.Index).Select(r => r.Step).ToList()
        };

        string recipeJson = JsonSerializer.Serialize(largeFileRecipe, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(patchDestPath + ".cdcrecipe", recipeJson);

        if (Directory.Exists(chunksOldDir)) Directory.Delete(chunksOldDir, true);
        if (Directory.Exists(chunksNewDir)) Directory.Delete(chunksNewDir, true);
    }

    public static void CreateHashNamedBaseline(string baselinePath)
    {
        string hash = GetFileSHA256(baselinePath);
        string ext = Path.GetExtension(baselinePath);
        File.Copy(baselinePath, Path.Combine(AppContext.BaseDirectory, "OutPatch", hash + ext.ToLower()), true);
        Console.WriteLine($"Baseline copied: {hash}{ext.ToLower()}");
    }

    private static string GetFileSHA256(string filePath)
    {
        using (var sha256 = SHA256.Create())
        using (var fs = File.OpenRead(filePath))
        {
            byte[] hash = sha256.ComputeHash(fs);
            var sb = new StringBuilder(64);
            foreach (byte b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}

public class ChunkInfo
{
    public int Index { get; set; }
    public long Offset { get; set; }
    public long Length { get; set; }
    public string Sha256 { get; set; } = string.Empty;
}

public class LargeFileRecipe
{
    public List<LargeFileRecipeStep> Steps { get; set; } = new();
}

public class LargeFileRecipeStep
{
    public string Action { get; set; } = string.Empty;
    public long SourceOffset { get; set; }
    public long SourceLength { get; set; }
    public string DeltaFileName { get; set; } = string.Empty;
}

public static class ContentDefinedChunker
{
    private const int MinChunkSize = 5 * 1024 * 1024;
    private const int MaxChunkSize = 10 * 1024 * 1024;
    private const int WindowSize = 48;
    private static readonly uint[] BuzTable = InitializeBuzTable();

    private static uint[] InitializeBuzTable()
    {
        var table = new uint[256];
        var rand = new Random(1337);
        for (int i = 0; i < 256; i++) table[i] = (uint)rand.Next();
        return table;
    }

    public static unsafe List<ChunkInfo> ChunkFile(string filePath, string outputDir)
    {
        Directory.CreateDirectory(outputDir);

        using var fs = File.OpenRead(filePath);
        byte[] window = new byte[WindowSize];
        int windowIndex = 0;
        uint hash = 0;

        long chunkStartOffset = 0;
        int chunkIndex = 0;
        uint boundaryMask = 0x013FFFFF;

        byte[] readBuffer = new byte[8 * 1024 * 1024];
        int bytesRead;

        byte[] chunkBuffer = new byte[MaxChunkSize];
        int chunkBufferPos = 0;

        List<Task<ChunkInfo>> processingTasks = new List<Task<ChunkInfo>>();

        fixed (uint* pBuz = BuzTable)
        fixed (byte* pWindow = window)
        fixed (byte* pChunk = chunkBuffer)
        {
            while ((bytesRead = fs.Read(readBuffer, 0, readBuffer.Length)) > 0)
            {
                fixed (byte* pRead = readBuffer)
                {
                    byte* pReadPtr = pRead;
                    byte* pReadEnd = pRead + bytesRead;

                    while (pReadPtr < pReadEnd)
                    {
                        byte b = *pReadPtr;
                        pReadPtr++;

                        pChunk[chunkBufferPos] = b;
                        chunkBufferPos++;

                        byte oldByte = pWindow[windowIndex];
                        pWindow[windowIndex] = b;
                        windowIndex = (windowIndex + 1) % WindowSize;

                        hash = ((hash << 1) | (hash >> 31)) ^ pBuz[b] ^ pBuz[oldByte];

                        if (chunkBufferPos >= MaxChunkSize || (chunkBufferPos >= MinChunkSize && (hash & boundaryMask) == 0))
                        {
                            byte[] chunkCopy = new byte[chunkBufferPos];
                            Buffer.BlockCopy(chunkBuffer, 0, chunkCopy, 0, chunkBufferPos);

                            int capturedIndex = chunkIndex;
                            long capturedOffset = chunkStartOffset;

                            processingTasks.Add(Task.Run(() => ProcessChunkOffThread(chunkCopy, outputDir, capturedIndex, capturedOffset)));

                            chunkStartOffset += chunkBufferPos;
                            chunkIndex++;
                            chunkBufferPos = 0;
                        }
                    }
                }
            }
        }

        if (chunkBufferPos > 0)
        {
            byte[] chunkCopy = new byte[chunkBufferPos];
            Buffer.BlockCopy(chunkBuffer, 0, chunkCopy, 0, chunkBufferPos);
            processingTasks.Add(Task.Run(() => ProcessChunkOffThread(chunkCopy, outputDir, chunkIndex, chunkStartOffset)));
        }

        Task.WaitAll(processingTasks.ToArray());
        return processingTasks.Select(t => t.Result).OrderBy(c => c.Index).ToList();
    }

    private static ChunkInfo ProcessChunkOffThread(byte[] chunkData, string outputDir, int index, long offset)
    {
        string hash = GetByteArraySHA256(chunkData);
        File.WriteAllBytes(Path.Combine(outputDir, $"{hash}.chunk"), chunkData);
        return new ChunkInfo { Index = index, Offset = offset, Length = chunkData.Length, Sha256 = hash };
    }

    private static string GetByteArraySHA256(byte[] data)
    {
        using var sha = SHA256.Create();
        byte[] hashBytes = sha.ComputeHash(data);
        var sb = new StringBuilder(64);
        foreach (byte b in hashBytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}