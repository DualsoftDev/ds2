module AasxDuplicateTests

open System
open System.IO
open System.IO.Compression
open Xunit
open Xunit.Abstractions
open Ds2.Core
open Ds2.Aasx

type AasxDuplicateTests(output: ITestOutputHelper) =

    [<Fact>]
    let ``Export should not create duplicate thumbnail entries`` () =
        let testFile = "/mnt/c/ds/NewProject3.1.aasx"
        let outputFile = "/mnt/c/ds/test_no_duplicate.aasx"

        if not (File.Exists(testFile)) then
            output.WriteLine($"Test file not found: {testFile}")
            Assert.True(false, $"Test file not found: {testFile}")

        output.WriteLine("=".PadRight(80, '='))
        output.WriteLine("Testing duplicate thumbnail fix")
        output.WriteLine("=".PadRight(80, '='))
        output.WriteLine($"Input: {testFile}")
        output.WriteLine($"Output: {outputFile}")
        output.WriteLine("")

        // Import
        output.WriteLine("1. Importing AASX...")
        let store = Ds2.Core.DsStore()
        let importResult = Ds2.Aasx.Import.Entry.importIntoStoreWithError store testFile

        match importResult with
        | Error msg ->
            output.WriteLine($"   Import failed: {msg}")
            Assert.True(false, $"Import failed: {msg}")
        | Ok () ->
            output.WriteLine("   Import successful")
            let projectCount = store.Projects.Count
            output.WriteLine($"   Projects loaded: {projectCount}")

            if projectCount = 0 then
                Assert.True(false, "No projects found in imported store")

            let project = store.Projects.Values |> Seq.head
            output.WriteLine($"   Project: {project.Name}")
            output.WriteLine($"   Has OriginalAasxEnvironment: {project.OriginalAasxEnvironment.IsSome}")
            output.WriteLine($"   Has OriginalAasxEntries: {project.OriginalAasxEntries.IsSome}")

            if project.OriginalAasxEntries.IsSome then
                output.WriteLine($"   Original entries count: {project.OriginalAasxEntries.Value.Count}")
            output.WriteLine("")

            // Export
            output.WriteLine("2. Exporting to new file...")
            if File.Exists(outputFile) then
                File.Delete(outputFile)

            Ds2.Aasx.Export.Entry.exportToAasxFile store project "https://dualsoft.com" outputFile

            Assert.True(File.Exists(outputFile), "Output file should exist")
            output.WriteLine("   Export complete")
            output.WriteLine("")

        // Analyze output
        output.WriteLine("3. Analyzing output file...")
        use fileStream = new FileStream(outputFile, FileMode.Open, FileAccess.Read, FileShare.Read)
        use archive = new ZipArchive(fileStream, ZipArchiveMode.Read)

        output.WriteLine($"   Total entries: {archive.Entries.Count}")
        output.WriteLine("")

        let entriesByName =
            archive.Entries
            |> Seq.groupBy (fun e -> e.FullName)
            |> Seq.map (fun (name, entries) -> (name, Seq.toList entries))
            |> Seq.toList

        output.WriteLine("   Entry details:")
        let mutable hasDuplicates = false

        for (name, entries) in entriesByName do
            let count = List.length entries
            if count > 1 then
                output.WriteLine($"   ❌ {name} - DUPLICATE ({count} times)")
                hasDuplicates <- true

                // Show details of each duplicate
                for (i, entry) in List.indexed entries do
                    output.WriteLine($"      [{i+1}] CompressedLength={entry.CompressedLength}, Length={entry.Length}")
            else
                output.WriteLine($"   ✓ {name}")

        output.WriteLine("")
        output.WriteLine("=".PadRight(80, '='))

        if hasDuplicates then
            output.WriteLine("❌ TEST FAILED: Duplicate entries found!")
            Assert.True(false, "Duplicate entries found in exported AASX file")
        else
            output.WriteLine("✅ TEST PASSED: No duplicate entries!")

        output.WriteLine("=".PadRight(80, '='))
