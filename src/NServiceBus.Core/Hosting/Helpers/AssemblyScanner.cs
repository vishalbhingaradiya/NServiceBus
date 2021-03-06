namespace NServiceBus.Hosting.Helpers
{
    using System;
    using System.Collections.Generic;
    using System.Configuration;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Runtime.CompilerServices;
    using System.Text;

    /// <summary>
    ///   Helpers for assembly scanning operations
    /// </summary>
    public class AssemblyScanner
    {
        public AssemblyScanner()
            : this(AppDomain.CurrentDomain.BaseDirectory)
        {
        }

        public AssemblyScanner(string baseDirectoryToScan)
        {
            AssembliesToInclude = new List<string>();
            AssembliesToSkip = new List<string>();
            MustReferenceAtLeastOneAssembly = new List<Assembly>();
            this.baseDirectoryToScan = baseDirectoryToScan;
            SetScanNestedDirectories();
            SetIncludeExesInScan();
        }

        public List<Assembly> MustReferenceAtLeastOneAssembly { get; private set; }

        [ObsoleteEx(
            RemoveInVersion = "5.0",
            Message = @"Defaults to scan sub-directories. In the future, 'ScanNestedDirectories' will be opt-in.")]
        void SetScanNestedDirectories()
        {
            bool scanNestedDirectories;
            var appSetting = ConfigurationManager.AppSettings["NServiceBus/AssemblyScanning/ScanNestedDirectories"];
            if (bool.TryParse(appSetting, out scanNestedDirectories))
            {
                ScanNestedDirectories = scanNestedDirectories;
            }
        }

        [ObsoleteEx(
            RemoveInVersion = "5.0",
            Message = @"Defaults to pick up .exe files. In the future, 'IncludeExesInScan' will be opt-in.")]
        void SetIncludeExesInScan()
        {
            bool includeExesInScan;
            var appSetting = ConfigurationManager.AppSettings["NServiceBus/AssemblyScanning/IncludeExesInScan"];
            if (bool.TryParse(appSetting, out includeExesInScan))
            {
                IncludeExesInScan = includeExesInScan;
            }
        }

        /// <summary>
        ///     Traverses the specified base directory including all sub-directories, generating a list of assemblies that can be
        ///     scanned for handlers, a list of skipped files, and a list of errors that occurred while scanning.
        ///     Scanned files may be skipped when they're either not a .NET assembly, or if a reflection-only load of the .NET
        ///     assembly reveals that it does not reference NServiceBus.
        /// </summary>
        public AssemblyScannerResults GetScannableAssemblies()
        {
            var results = new AssemblyScannerResults();

            if (IncludeAppDomainAssemblies)
            {
                var matchingAssembliesFromAppDomain = AppDomain.CurrentDomain
                                                               .GetAssemblies()
                                                               .Where(assembly => IsIncluded(assembly.GetName().Name));

                results.Assemblies.AddRange(matchingAssembliesFromAppDomain);
            }

            foreach (var assemblyFile in ScanDirectoryForAssemblyFiles())
            {
                ScanAssembly(assemblyFile.FullName, results);
            }

            return results;
        }

        void ScanAssembly(string assemblyPath, AssemblyScannerResults results)
        {
            Assembly assembly;

            if (!IsIncluded(Path.GetFileNameWithoutExtension(assemblyPath)))
            {
                var skippedFile = new SkippedFile(assemblyPath, "File was explicitly excluded from scanning.");
                results.SkippedFiles.Add(skippedFile);
                return;
            }

            var compilationMode = Image.GetCompilationMode(assemblyPath);
            if (compilationMode == Image.CompilationMode.NativeOrInvalid)
            {
                var skippedFile = new SkippedFile(assemblyPath, "File is not a .NET assembly.");
                results.SkippedFiles.Add(skippedFile);
                return;
            }

            if (!Environment.Is64BitProcess && compilationMode == Image.CompilationMode.CLRx64)
            {
                var skippedFile = new SkippedFile(assemblyPath, "x64 .NET assembly can't be loaded by a 32Bit process.");
                results.SkippedFiles.Add(skippedFile);
                return;
            }

            try
            {
                //TODO: re-enable when we make message scanning lazy #1617
                //if (!AssemblyPassesReferencesTest(assemblyPath))
                //{
                //    var skippedFile = new SkippedFile(assemblyPath, "Assembly does not reference at least one of the must referenced assemblies.");
                //    results.SkippedFiles.Add(skippedFile);
                //    return;
                //}
                if (IsRuntimeAssembly(assemblyPath))
                {
                    var skippedFile = new SkippedFile(assemblyPath, "Assembly .net runtime assembly.");
                    results.SkippedFiles.Add(skippedFile);
                    return;
                }

                assembly = Assembly.LoadFrom(assemblyPath);
                if (results.Assemblies.Contains(assembly))
                {
                    return;
                }
            }
            catch (BadImageFormatException)
            {
                var errorMessage = String.Format("Could not load '{0}'. Consider excluding that assembly from the scanning.", assemblyPath);
                results.Errors.Add(errorMessage);
                return;
            }

            try
            {
                //will throw if assembly cannot be loaded
                assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                var errorMessage = FormatReflectionTypeLoadException(assemblyPath, e);
                results.Errors.Add(errorMessage);
                return;
            }

            results.Assemblies.Add(assembly);
        }


        internal static bool IsRuntimeAssembly(string assemblyPath)
        {
            var publicKeyToken = AssemblyName.GetAssemblyName(assemblyPath).GetPublicKeyToken();
            var lowerInvariant = BitConverter.ToString(publicKeyToken).Replace("-", "").ToLowerInvariant();
            //System
            if (lowerInvariant == "b77a5c561934e089")
            {
                return true;
            }

            //Web
            if (lowerInvariant == "b03f5f7f11d50a3a")
            {
                return true;
            }
            
            //patterns and practices
            if (lowerInvariant == "31bf3856ad364e35")
            {
                return true;
            }
            
            return false;
        }

        internal static string FormatReflectionTypeLoadException(string fileName, ReflectionTypeLoadException e)
        {
            var sb = new StringBuilder();

            sb.AppendLine(string.Format("Could not enumerate all types for '{0}'. Exception message: {1}.", fileName, e));

            if (!e.LoaderExceptions.Any())
            {
                return sb.ToString();
            }

            sb.AppendLine("Scanned type errors:");
            foreach (var ex in e.LoaderExceptions)
            {
                sb.AppendLine(ex.ToString());
            }

            return sb.ToString();
        }

        IEnumerable<FileInfo> ScanDirectoryForAssemblyFiles()
        {
            var baseDir = new DirectoryInfo(baseDirectoryToScan);
            var searchOption = ScanNestedDirectories ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            return GetFileSearchPatternsToUse()
                .SelectMany(extension => baseDir.GetFiles(extension, searchOption));
        }

        IEnumerable<string> GetFileSearchPatternsToUse()
        {
            yield return "*.dll";

            if (IncludeExesInScan)
            {
                yield return "*.exe";
            }
        }

        bool AssemblyPassesReferencesTest(string assemblyPath)
        {
            if (MustReferenceAtLeastOneAssembly.Count == 0)
            {
                return true;
            }
            var lightLoad = Assembly.ReflectionOnlyLoadFrom(assemblyPath);
            var referencedAssemblies = lightLoad.GetReferencedAssemblies();

            return MustReferenceAtLeastOneAssembly
                .Select(reference => reference.GetName().Name)
                .Any(name => referencedAssemblies.Any(a => a.Name == name));
        }

        /// <summary>
        ///  Determines whether the specified assembly name or file name can be included, given the set up include/exclude
        ///  patterns and default include/exclude patterns
        /// </summary>
        bool IsIncluded(string assemblyNameOrFileName)
        {
            var isExplicitlyExcluded = AssembliesToSkip.Any(excluded => IsMatch(excluded, assemblyNameOrFileName));
            if (isExplicitlyExcluded)
            {
                return false;
            }

            var isExcludedByDefault = DefaultAssemblyExclusions.Any(exclusion => IsMatch(exclusion, assemblyNameOrFileName));
            if (isExcludedByDefault)
            {
                return false;
            }

            var noAssembliesWereExplicitlyIncluded = !AssembliesToInclude.Any();
            var isExplicitlyIncluded = AssembliesToInclude.Any(included => IsMatch(included, assemblyNameOrFileName));

            return noAssembliesWereExplicitlyIncluded || isExplicitlyIncluded;
        }

        static bool IsMatch(string expression, string scopedNameOrFileName)
        {
            if (DistillLowerAssemblyName(scopedNameOrFileName).StartsWith(expression.ToLower()))
            {
                return true;
            }

            if (DistillLowerAssemblyName(expression).TrimEnd('.') == DistillLowerAssemblyName(scopedNameOrFileName))
            {
                return true;
            }

            return false;
        }

        internal static bool IsAllowedType(Type type)
        {
            return !type.IsValueType &&
                   !(type.GetCustomAttributes(typeof(CompilerGeneratedAttribute), false).Length > 0);
        }

        static string DistillLowerAssemblyName(string assemblyOrFileName)
        {
            var lowerAssemblyName = assemblyOrFileName.ToLowerInvariant();
            if (lowerAssemblyName.EndsWith(".dll"))
            {
                lowerAssemblyName = lowerAssemblyName.Substring(0, lowerAssemblyName.Length - 4);
            }
            return lowerAssemblyName;
        }

        string baseDirectoryToScan;
        internal List<string> AssembliesToInclude;
        internal List<string> AssembliesToSkip;

        internal bool IncludeAppDomainAssemblies;
        internal bool IncludeExesInScan = true;
        internal bool ScanNestedDirectories = true;



        //TODO: delete when we make message scanning lazy #1617
        static string[] DefaultAssemblyExclusions =
                                {

                                    "system.",
                                    "mscorlib.", 
                  
                                    // NSB Build-Dependencies
                                    "nunit.", "pnunit.", "rhino.mocks.", "XsdGenerator.",
                 
                                    // NSB OSS Dependencies
                                    "rhino.licensing.", "bouncycastle.crypto",
                                    "magnum.", "interop.", "nlog.", "newtonsoft.json.",
                                    "common.logging.", "topshelf.",
                                    "Autofac.", "log4net.", "nhibernate.", 
                                    "castle.",

                                    // Raven
                                    "raven.server", "raven.client", "raven.munin.",
                                    "raven.storage.", "raven.abstractions.", "raven.database",
                                    "esent.interop", "asyncctplibrary.", "lucene.net.",
                                    "icsharpcode.nrefactory", "spatial4n.core",

                                    // Azure host process, which is typically referenced for ease of deployment but should not be scanned
                                    "NServiceBus.Hosting.Azure.HostProcess.exe",

                                    // And other windows azure stuff
                                    "Microsoft.WindowsAzure.",

                                };
    }
}