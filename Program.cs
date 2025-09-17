using DeltaQ.BsDiff;
using DeltaQ.SuffixSorting;
using DeltaQ.SuffixSorting.LibDivSufSort;

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
            Console.WriteLine("Enter Baseline File Name");

            string baselineFileName = Console.ReadLine();
            baselineFileName.Trim();

            string baseLinePath = AppContext.BaseDirectory + "/PatchZone/" + baselineFileName;

            Console.WriteLine("Enter Updated File Name");

            string updatedFileName = Console.ReadLine();
            updatedFileName.Trim();

            string updatedPath = AppContext.BaseDirectory + "/PatchZone/" + updatedFileName;

            CreatePatch(baseLinePath, updatedPath);

            Console.WriteLine("Patch Created");
        }
    }

    public static void CreatePatch(string baselinePath, string updatedPath)
    {
        var oldData = File.ReadAllBytes(baselinePath);
        var newData = File.ReadAllBytes(updatedPath);

        using var outStream = File.Create(AppContext.BaseDirectory + "/OutPatch/" + "PatchFile.delta");

        ISuffixSort suffixSorter = new LibDivSufSort();

        Diff.Create(oldData,newData, outStream, suffixSorter);
    }
}