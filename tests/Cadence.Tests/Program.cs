using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace Cadence.Tests
{
    internal static class Program
    {
        static Program()
        {
            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
            {
                string dllName = new AssemblyName(args.Name).Name + ".dll";

                // Search in src/lib and application base
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string[] searchPaths = new[]
                {
                    Path.Combine(baseDir, dllName),
                    Path.Combine(baseDir, "..", "..", "..", "src", "lib", dllName),
                    Path.Combine(baseDir, "..", "..", "src", "lib", dllName),
                    @"C:\Users\User\Documents\Games\GTA 5 Plugin Workspace\01_MAIN_DEVELOPMENT\Cadence\src\lib\" + dllName
                };

                foreach (string candidate in searchPaths)
                {
                    try
                    {
                        if (File.Exists(candidate))
                        {
                            return Assembly.LoadFrom(candidate);
                        }
                    }
                    catch { }
                }

                return null;
            };
        }

        private static int Main(string[] args)
        {
            Console.WriteLine("================================================================================");
            Console.WriteLine("                 CADENCE AUTOMATED TEST SUITE (.NET 4.8)                        ");
            Console.WriteLine("================================================================================");

            var stopwatch = Stopwatch.StartNew();
            int failedSuites = 0;
            int totalSuites = 3;

            // 1. Settings Tests
            try
            {
                SettingsTests.RunAll();
            }
            catch (Exception ex)
            {
                failedSuites++;
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[FAILED] SettingsTests Suite threw: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
                Console.ResetColor();
            }

            // 2. UnitRegistry Tests
            try
            {
                UnitRegistryTests.RunAll();
            }
            catch (Exception ex)
            {
                failedSuites++;
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[FAILED] UnitRegistryTests Suite threw: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
                Console.ResetColor();
            }

            // 3. Packaging Tests
            try
            {
                PackagingTests.RunAll();
            }
            catch (Exception ex)
            {
                failedSuites++;
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[FAILED] PackagingTests Suite threw: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
                Console.ResetColor();
            }

            stopwatch.Stop();
            Console.WriteLine("\n--------------------------------------------------------------------------------");
            Console.WriteLine($"Test Execution Finished in {stopwatch.ElapsedMilliseconds} ms");

            if (failedSuites == 0)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[ALL PASS] {totalSuites}/{totalSuites} test suites passed cleanly (0 failures).");
                Console.ResetColor();
                Console.WriteLine("================================================================================");
                return 0;
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[FAILURES DETECTED] {failedSuites}/{totalSuites} test suites failed.");
                Console.ResetColor();
                Console.WriteLine("================================================================================");
                return 1;
            }
        }
    }
}
