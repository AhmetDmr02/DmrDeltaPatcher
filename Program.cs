using DeltaQ.BsDiff;
using DeltaQ.SuffixSorting.LibDivSufSort;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

public static class Program
{
    public static void Main(string[] args)
    {
        string patchZoneDir = Path.Combine(AppContext.BaseDirectory, "PatchZone");
        string outPatchDir = Path.Combine(AppContext.BaseDirectory, "OutPatch");

        if (!Directory.Exists(patchZoneDir)) Directory.CreateDirectory(patchZoneDir);
        if (!Directory.Exists(outPatchDir)) Directory.CreateDirectory(outPatchDir);

        while (true)
        {
            Console.Clear();
            Console.WriteLine("== PATCH CREATOR ==");
            Console.WriteLine("1. Create Patch");
            Console.WriteLine("2. Generate Baseline Hash Copy");
            Console.WriteLine("3. Exit");
            Console.Write("\nSelect Mode: ");

            string choice = (Console.ReadLine() ?? "").Trim();
            if (choice == "3") break;

            if (choice == "1")
            {
                Console.WriteLine("Enter Baseline ZIP Name (e.g., baseline.zip):");
                string baseLinePath = Path.Combine(patchZoneDir, (Console.ReadLine() ?? "").Trim());

                Console.WriteLine("Enter Updated ZIP Name (e.g., updated.zip):");
                string updatedPath = Path.Combine(patchZoneDir, (Console.ReadLine() ?? "").Trim());

                if (!File.Exists(baseLinePath) || !File.Exists(updatedPath))
                {
                    Console.WriteLine("Files missing! Press any key...");
                    Console.ReadKey();
                    continue;
                }

                CreateSmartPatch(baseLinePath, updatedPath);
            }
            else if (choice == "2")
            {
                Console.WriteLine("Enter Baseline File Name:");
                string baseLinePath = Path.Combine(patchZoneDir, (Console.ReadLine() ?? "").Trim());
                if (File.Exists(baseLinePath)) CreateHashNamedBaseline(baseLinePath);
            }
        }
    }

    public static void CreateSmartPatch(string baselineZip, string updatedZip)
    {
        string tempDir = Path.Combine(AppContext.BaseDirectory, "TempProcessing");
        string oldExtract = Path.Combine(tempDir, "Old");
        string newExtract = Path.Combine(tempDir, "New");
        string patchBuildDir = Path.Combine(tempDir, "PatchContainer");

        if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        Directory.CreateDirectory(oldExtract);
        Directory.CreateDirectory(newExtract);
        Directory.CreateDirectory(patchBuildDir);

        Console.WriteLine("Extracting archives for file-by-file comparison...");
        ZipFile.ExtractToDirectory(baselineZip, oldExtract);
        ZipFile.ExtractToDirectory(updatedZip, newExtract);

        List<string> deletions = new List<string>();
        var oldFiles = Directory.GetFiles(oldExtract, "*.*", SearchOption.AllDirectories);
        var newFiles = Directory.GetFiles(newExtract, "*.*", SearchOption.AllDirectories);

        var oldRelativePaths = oldFiles.Select(f => Path.GetRelativePath(oldExtract, f)).ToHashSet();
        var newRelativePaths = newFiles.Select(f => Path.GetRelativePath(newExtract, f)).ToHashSet();

        Console.WriteLine("Analyzing structural changes...");
        var suffixSorter = new LibDivSufSort();

        // 1. Check for Additions and Modifications
        foreach (string newRelPath in newRelativePaths)
        {
            string newFilePath = Path.Combine(newExtract, newRelPath);
            string oldFilePath = Path.Combine(oldExtract, newRelPath);
            string patchDestPath = Path.Combine(patchBuildDir, newRelPath);

            Directory.CreateDirectory(Path.GetDirectoryName(patchDestPath)!);

            if (oldRelativePaths.Contains(newRelPath))
            {
                // File exists in both. Check if it changed.
                if (GetFileSHA256(oldFilePath) != GetFileSHA256(newFilePath))
                {
                    if (GetFileSHA256(oldFilePath) != GetFileSHA256(newFilePath))
                    {
                        Console.WriteLine($"[MODIFIED] Diffing {newRelPath}...");

                        byte[] oldFs = File.ReadAllBytes(oldFilePath);
                        byte[] newFs = File.ReadAllBytes(newFilePath);

                        using (var outStream = File.Create(patchDestPath + ".delta"))
                        {
                            DeltaQ.BsDiff.Diff.Create(oldFs, newFs, outStream, suffixSorter);
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

        if (deletions.Count > 0)
        {
            File.WriteAllLines(Path.Combine(patchBuildDir, "dmr_deletions.txt"), deletions);
        }

        Console.WriteLine("\nPackaging smart patch container...");
        string tempPatchZip = Path.Combine(tempDir, "temp_patch.zip");
        ZipFile.CreateFromDirectory(patchBuildDir, tempPatchZip, CompressionLevel.Optimal, false);

        string patchHash = GetFileSHA256(tempPatchZip);
        string finalPatchPath = Path.Combine(AppContext.BaseDirectory, "OutPatch", patchHash + ".patch");

        if (File.Exists(finalPatchPath)) File.Delete(finalPatchPath);
        File.Move(tempPatchZip, finalPatchPath);

        Console.WriteLine("Cleaning up temporary workspace...");
        Directory.Delete(tempDir, true);

        Console.WriteLine($"\nSmart Patch generated completely: {patchHash}.patch");
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