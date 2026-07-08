using DeltaQ.BsDiff;
using DeltaQ.SuffixSorting;
using DeltaQ.SuffixSorting.LibDivSufSort;
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

public static class Program
{
    public static void Main(string[] args)
    {
        if (!Directory.Exists(AppContext.BaseDirectory + "/PatchZone"))
        {
            Directory.CreateDirectory(AppContext.BaseDirectory + "/PatchZone");
        }
        if (!Directory.Exists(AppContext.BaseDirectory + "/OutPatch"))
        {
            Directory.CreateDirectory(AppContext.BaseDirectory + "/OutPatch");
        }

        while (true)
        {
            Console.Clear();
            Console.WriteLine("=== DMR PATCH CREATOR TOOL ===");
            Console.WriteLine("1. Create Delta Patch (Auto-named with SHA256)");
            Console.WriteLine("2. Generate Baseline Hash Copy (Auto-named with SHA256)");
            Console.WriteLine("3. Exit");
            Console.Write("\nSelect Mode: ");

            string choice = Console.ReadLine() ?? "";
            choice = choice.Trim();

            if (choice == "3") break;

            if (choice == "1")
            {
                Console.WriteLine("\n[MODE 1: DELTA PATCH CREATION]");
                Console.WriteLine("Enter Baseline File Name (e.g., baseline.zip):");
                string baselineFileName = Console.ReadLine() ?? "";
                baselineFileName = baselineFileName.Trim();
                string baseLinePath = AppContext.BaseDirectory + "/PatchZone/" + baselineFileName;

                Console.WriteLine("Enter Updated File Name (e.g., updated.zip):");
                string updatedFileName = Console.ReadLine() ?? "";
                updatedFileName = updatedFileName.Trim();
                string updatedPath = AppContext.BaseDirectory + "/PatchZone/" + updatedFileName;

                if (!File.Exists(baseLinePath) || !File.Exists(updatedPath))
                {
                    Console.WriteLine("Error: Source files could not be found inside PatchZone directory! Press any key to continue...");
                    Console.ReadKey();
                    continue;
                }

                CreatePatch(baseLinePath, updatedPath);
            }
            else if (choice == "2")
            {
                Console.WriteLine("\n[MODE 2: BASELINE HASH GENERATION]");
                Console.WriteLine("Enter Baseline File Name to rename with Hash:");
                string baselineFileName = Console.ReadLine() ?? "";
                baselineFileName = baselineFileName.Trim();
                string baseLinePath = AppContext.BaseDirectory + "/PatchZone/" + baselineFileName;

                if (!File.Exists(baseLinePath))
                {
                    Console.WriteLine("Error: Source baseline file could not be found inside PatchZone! Press any key to continue...");
                    Console.ReadKey();
                    continue;
                }

                CreateHashNamedBaseline(baseLinePath);
            }
            else
            {
                Console.WriteLine("Invalid selection. Press any key to retry...");
                Console.ReadKey();
                continue;
            }

            Console.WriteLine("\nOperation completed successfully! Press any key to return to menu...");
            Console.ReadKey();
        }
    }

    public static void CreatePatch(string baselinePath, string updatedPath)
    {
        Console.WriteLine("Reading files into memory pipeline...");
        var oldData = File.ReadAllBytes(baselinePath);
        var newData = File.ReadAllBytes(updatedPath);

        string tempPatchPath = Path.Combine(AppContext.BaseDirectory, "OutPatch", "temp_building.delta");
        if (File.Exists(tempPatchPath)) File.Delete(tempPatchPath);

        Console.WriteLine("Running LibDivSufSort suffix sort and calculating compression streams...");
        using (var outStream = File.Create(tempPatchPath))
        {
            ISuffixSort suffixSorter = new LibDivSufSort();
            Diff.Create(oldData, newData, outStream, suffixSorter);
        }

        Console.WriteLine("Calculating cryptographic SHA256 checksum signature...");
        string patchHash = GetFileSHA256(tempPatchPath);

        string finalPatchPath = Path.Combine(AppContext.BaseDirectory, "OutPatch", patchHash + ".delta");

        if (File.Exists(finalPatchPath)) File.Delete(finalPatchPath);
        File.Move(tempPatchPath, finalPatchPath);

        Console.WriteLine($"\nPatch File generated completely: {patchHash}.delta");
    }

    public static void CreateHashNamedBaseline(string baselinePath)
    {
        Console.WriteLine("Calculating baseline cryptographic SHA256 checksum signature...");
        string baselineHash = GetFileSHA256(baselinePath);
        string fileExtension = Path.GetExtension(baselinePath);

        string finalBaselinePath = Path.Combine(AppContext.BaseDirectory, "OutPatch", baselineHash + fileExtension.ToLower());

        Console.WriteLine("Copying asset file to destination mirror directory...");
        File.Copy(baselinePath, finalBaselinePath, true);

        Console.WriteLine($"\nBaseline asset successfully copied: {baselineHash}{fileExtension.ToLower()}");
    }

    private static string GetFileSHA256(string filePath)
    {
        using (SHA256 sha256Hash = SHA256.Create())
        using (FileStream fs = File.OpenRead(filePath))
        {
            byte[] hashBytes = sha256Hash.ComputeHash(fs);
            StringBuilder hashStringBuilder = new StringBuilder();

            foreach (byte b in hashBytes)
            {
                hashStringBuilder.Append(b.ToString("x2"));
            }

            return hashStringBuilder.ToString();
        }
    }
}