// Unity C# reference source
// Copyright (c) Unity Technologies. For terms of use, see
// https://unity3d.com/legal/licenses/Unity_Reference_Only_License

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Unity.Scripting.Compilation;
using UnityEditor.Compilation;
using UnityEditor.Modules;
using UnityEditor.Scripting.Compilers;
using UnityEditor.VisualStudioIntegration;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.Profiling;
using CompilerMessage = UnityEditor.Scripting.Compilers.CompilerMessage;
using CompilerMessageType = UnityEditor.Scripting.Compilers.CompilerMessageType;
using Directory = System.IO.Directory;
using File = System.IO.File;
using IOException = System.IO.IOException;

namespace UnityEditor.Scripting.ScriptCompilation
{
    class EditorCompilation
    {
        public enum CompileStatus
        {
            Idle,
            Compiling,
            CompilationStarted,
            CompilationFailed,
            CompilationComplete
        }

        public enum DeleteFileOptions
        {
            NoLogError = 0,
            LogError = 1,
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct TargetAssemblyInfo
        {
            public string Name;
            public AssemblyFlags Flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct AssemblyCompilerMessages
        {
            public string assemblyFilename;
            public CompilerMessage[] messages;
        }

        public struct CustomScriptAssemblyAndReference
        {
            public CustomScriptAssembly Assembly;
            public CustomScriptAssembly Reference;
        }

        [Flags]
        public enum CompileScriptAssembliesOptions
        {
            none = 0,
            skipSetupChecks = (1 << 0),
        }

        internal abstract class UnitySpecificCompilerMessageProcessor
        {
            public abstract bool IsInterestedInMessage(CompilerMessage m);
            public abstract void PostprocessMessage(ref CompilerMessage m);
        }

        class UnsafeErrorProcessor : UnitySpecificCompilerMessageProcessor
        {
            string unityUnsafeMessage;

            public UnsafeErrorProcessor(ScriptAssembly assembly, EditorCompilation editorCompilation)
            {
                var assemblyName = AssetPath.GetAssemblyNameWithoutExtension(assembly.Filename);

                try
                {
                    var customScriptAssembly = editorCompilation.FindCustomScriptAssemblyFromAssemblyName(assemblyName);
                    unityUnsafeMessage = string.Format("Enable \"Allow 'unsafe' code\" in the inspector for '{0}' to fix this error.", customScriptAssembly.FilePath);
                }
                catch
                {
                    unityUnsafeMessage = "Enable \"Allow 'unsafe' code\" in Player Settings to fix this error.";
                }
            }

            public override bool IsInterestedInMessage(CompilerMessage m)
            {
                return m.type == CompilerMessageType.Error && m.message.Contains("CS0227");
            }

            public override void PostprocessMessage(ref CompilerMessage m)
            {
                m.message += ". " + unityUnsafeMessage;
            }
        }

        internal class DeterministicAssemblyVersionErrorProcessor : UnitySpecificCompilerMessageProcessor
        {
            public override bool IsInterestedInMessage(CompilerMessage message)
            {
                if (message.type != CompilerMessageType.Error)
                {
                    return false;
                }

                if (message.message.IndexOf("error CS8357", StringComparison.Ordinal) >= 0)
                {
                    return true;
                }

                return false;
            }

            public override void PostprocessMessage(ref CompilerMessage message)
            {
                message.message = "Deterministic compilation failed. You can disable Deterministic builds in Player Settings\n" + message.message;
            }
        }

        class ModuleReferenceErrorProcessor : UnitySpecificCompilerMessageProcessor
        {
            Regex messageRegex;

            public ModuleReferenceErrorProcessor()
            {
                messageRegex = new Regex("[`']UnityEngine.(\\w*)Module,");
            }

            public override bool IsInterestedInMessage(CompilerMessage m)
            {
                return m.type == CompilerMessageType.Error && (m.message.Contains("CS1069") || m.message.Contains("CS1070"));
            }

            private static string GetNiceDisplayNameForModule(string name)
            {
                for (int i = 1; i < name.Length; i++)
                    if (char.IsLower(name[i - 1]) && !char.IsLower(name[i]))
                    {
                        name = name.Insert(i, " ");
                        i++;
                    }

                return name;
            }

            public override void PostprocessMessage(ref CompilerMessage message)
            {
                var match = messageRegex.Match(message.message);
                if (match.Success)
                {
                    var index = message.message.IndexOf("Consider adding a reference to that assembly.");
                    if (index != -1)
                        message.message = message.message.Substring(0, index);
                    var moduleName = match.Groups[1].Value;
                    moduleName = GetNiceDisplayNameForModule(moduleName);
                    message.message += string.Format("Enable the built in package '{0}' in the Package Manager window to fix this error.", moduleName);
                }
            }
        }

        class DirtyState
        {
            public bool AreAllTargetAssembliesDirty { get; private set; }
            public bool AreAllPrecompiledAssembliesDirty { get; private set; }
            public HashSet<TargetAssembly> DirtyTargetAssemblies { get; set; }
            public HashSet<string> DirtyPrecompiledAssemblies { get; }
            public bool IsDirty { get; set; } = false;

            public DirtyState()
            {
                DirtyTargetAssemblies = new HashSet<TargetAssembly>();
                DirtyPrecompiledAssemblies = new HashSet<string>();
            }

            public void ClearAll()
            {
                IsDirty = false;
                AreAllTargetAssembliesDirty = false;
                DirtyTargetAssemblies.Clear();
                DirtyPrecompiledAssemblies.Clear();
                AreAllPrecompiledAssembliesDirty = false;
            }

            public void RemoveCompiledTargetAssembly(string assemblyFilename)
            {
                var targetAssembly = DirtyTargetAssemblies.FirstOrDefault(a => a.Filename == assemblyFilename);

                // The compiled assembly might be in the dirty target assemblies
                // if it is a reference to a dirty target assemblies or
                // if AreAllTargetAssembliesDirty is true.
                if (targetAssembly != null)
                    DirtyTargetAssemblies.Remove(targetAssembly);
            }

            public void DirtyAllTargetAssemblies(IEnumerable<TargetAssembly> predefinedTargetAssembiles, IEnumerable<TargetAssembly> customTargetAssemblies)
            {
                DirtyTargetAssemblies.UnionWith(predefinedTargetAssembiles);
                DirtyTargetAssemblies.UnionWith(customTargetAssemblies);

                AreAllTargetAssembliesDirty = true;
                IsDirty = true;
            }

            public void DirtyAllPrecompiledAssemblies()
            {
                AreAllPrecompiledAssembliesDirty = true;
                IsDirty = true;
            }

            public void AddDirtyTargetAssembly(TargetAssembly assembly)
            {
                DirtyTargetAssemblies.Add(assembly);
                IsDirty = true;
            }

            public void DirtyPrecompiledAssembly(string assembly)
            {
                DirtyPrecompiledAssemblies.Add(assembly);
                IsDirty = true;
            }
        }

        public PrecompiledAssemblyProviderBase PrecompiledAssemblyProvider { get; set; } = new PrecompiledAssemblyProvider();
        public CompilationSetupErrorsTrackerBase CompilationSetupErrorsTracker { get; set; } = new CompilationSetupErrorsTracker();
        public ResponseFileProvider ResponseFileProvider { get; set; } = new MicrosoftCSharpResponseFileProvider();
        private FileIOProvider fileIOProvider = new FileIOProvider();
        private DirectoryIOProvider directoryIOProvider = new DirectoryIOProvider();

        Dictionary<object, Stopwatch> stopWatchDict = new Dictionary<object, Stopwatch>();
        string projectDirectory = string.Empty;
        Dictionary<string, string> allScripts = new Dictionary<string, string>();

        DirtyState dirtyState = new DirtyState();

        HashSet<string> runScriptUpdaterAssemblies = new HashSet<string>();
        bool recompileAllScriptsOnNextTick;
        CustomScriptAssembly[] customScriptAssemblies = new CustomScriptAssembly[0];
        Dictionary<string, CustomScriptAssembly> filePathToCustomScriptAssemblies = new Dictionary<string, CustomScriptAssembly>();

        List<CustomScriptAssemblyReference> customScriptAssemblyReferences = new List<CustomScriptAssemblyReference>();
        Dictionary<string, TargetAssembly> customTargetAssemblies = new Dictionary<string, TargetAssembly>(); // TargetAssemblies for customScriptAssemblies.
        PrecompiledAssembly[] unityAssemblies;
        CompilationTask compilationTask;
        string outputDirectory;
        string outputDirectoryEditor;
        bool skipCustomScriptAssemblyGraphValidation = false;
        List<AssemblyBuilder> assemblyBuilders = new List<Compilation.AssemblyBuilder>();
        HashSet<string> changedAssemblies = new HashSet<string>();
        int maxConcurrentCompilers = 0;
        CompilerFactory compilerFactory;

        static readonly string EditorTempPath = "Temp";

        private AssetPathMetaData[] m_AssetPathsMetaData;
        private Dictionary<string, VersionMetaData> m_VersionMetaDatas;

        public event Action<string> unusedAssembly;
        public event Action<string[]> dirtyPrecompiledAssembly;

        public event Action<object> compilationStarted;
        public event Action<object> compilationFinished;
        public event Action<string> assemblyCompilationStarted;
        public event Action<ScriptAssembly, UnityEditor.Compilation.CompilerMessage[], EditorScriptCompilationOptions> assemblyCompilationFinished;

        public Dictionary<string, TargetAssembly> CustomTargetAssemblies
        {
            get
            {
                return customTargetAssemblies;
            }
        }

        public IILPostProcessing ILPostProcessing;
        public bool IsRunningRoslynAnalysisSynchronously { get; private set; }

        static EditorCompilation() {}

        public void Initialize()
        {
            // Initialize maxConcurrentCompilers if it hasn't been set already.
            if (maxConcurrentCompilers == 0)
                SetMaxConcurrentCompilers(UnityEngine.SystemInfo.processorCount);

            compilerFactory = new CompilerFactory(new CompilerFactoryHelper());
            if (compilerFactory.CompilerChanged())
            {
                RecompileAllScriptsOnNextTick();
            }
            ILPostProcessing = new ILPostProcessing(this);
        }

        public void SetMaxConcurrentCompilers(int maxCompilers)
        {
            maxConcurrentCompilers = maxCompilers;
        }

        internal string GetAssemblyTimestampPath(string editorAssemblyPath)
        {
            return AssetPath.Combine(editorAssemblyPath, "BuiltinAssemblies.stamp");
        }

        internal void SetProjectDirectory(string projectDirectory)
        {
            this.projectDirectory = projectDirectory;
        }

        internal void SetAssetPathsMetaData(AssetPathMetaData[] assetPathMetaDatas)
        {
            m_AssetPathsMetaData = assetPathMetaDatas;

            var versionMetaDataComparer = new VersionMetaDataComparer();

            m_VersionMetaDatas = assetPathMetaDatas ?
                .Where(x => x.VersionMetaData != null)
                .Select(x => x.VersionMetaData)
                .Distinct(versionMetaDataComparer)
                .ToDictionary(x => x.Name, x => x);
            UpdateCustomTargetAssembliesAssetPathsMetaData(customScriptAssemblies, assetPathMetaDatas, forceUpdate: true);
        }

        internal void SetAdditionalVersionMetaDatas(VersionMetaData[] versionMetaDatas)
        {
            Assert.IsTrue(m_VersionMetaDatas != null, "EditorCompilation.SetAssetPathsMetaData() must be called before EditorCompilation.SetAdditionalVersionMetaDatas()");
            foreach (var versionMetaData in versionMetaDatas)
                m_VersionMetaDatas[versionMetaData.Name] = versionMetaData;
        }

        internal AssetPathMetaData[] GetAssetPathsMetaData()
        {
            return m_AssetPathsMetaData;
        }

        internal Dictionary<string, VersionMetaData> GetVersionMetaDatas()
        {
            return m_VersionMetaDatas;
        }

        public void SetAllScripts(string[] allScripts, string[] assemblyFilenames)
        {
            this.allScripts = new Dictionary<string, string>();

            for (int i = 0; i < allScripts.Length; ++i)
            {
                this.allScripts[allScripts[i]] = assemblyFilenames[i];
            }
        }

        public bool IsExtensionSupportedByCompiler(string extension)
        {
            var languages = ScriptCompilers.SupportedLanguages;
            return languages.Count(l => l.GetExtensionICanCompile() == extension) > 0;
        }

        public string[] GetExtensionsSupportedByCompiler()
        {
            var languages = ScriptCompilers.SupportedLanguages;
            return languages.Select(language => language.GetExtensionICanCompile()).ToArray();
        }

        public void DirtyPredefinedAssemblyScripts(EditorScriptCompilationOptions options, BuildTargetGroup platformGroup, BuildTarget platform)
        {
            var scriptAssemblySettings = CreateScriptAssemblySettings(platformGroup, platform, options);
            var scriptAssemblies = GetAllScriptAssembliesOfType(scriptAssemblySettings, TargetAssemblyType.Predefined);

            foreach (var assembly in scriptAssemblies)
            {
                foreach (var script in assembly.Files)
                {
                    var assemblyName = allScripts[script];

                    var targetAssembly = EditorBuildRules.GetTargetAssembly(script, assemblyName, projectDirectory, customTargetAssemblies);
                    dirtyState.AddDirtyTargetAssembly(targetAssembly);
                }
            }
        }

        private void AddChangedAssembly(string name, EditorScriptCompilationOptions compilationOptions)
        {
            if ((compilationOptions & EditorScriptCompilationOptions.BuildingForEditor) == EditorScriptCompilationOptions.BuildingForEditor)
            {
                changedAssemblies.Add(name);
            }
        }

        public string[] GetChangedAssemblies()
        {
            return changedAssemblies.ToArray();
        }

        public void DirtyAllScripts()
        {
            // DirtyAllScripts is called in cases where defines change ect. so we want to clear the cached
            // precompiled assemblies, as their compatibility can have changed.
            // DirtyAllScripts is the same as a full recompilation
            PrecompiledAssemblyProvider.Dirty();

            if (allScripts.Count > 0)
                dirtyState.DirtyAllTargetAssemblies(EditorBuildRules.GetPredefinedTargetAssemblies(), customTargetAssemblies.Values);
        }

        public void DirtyScript(string path, string assemblyFilename)
        {
            allScripts[path] = assemblyFilename;

            var targetAssembly = EditorBuildRules.GetTargetAssembly(path, assemblyFilename, projectDirectory, customTargetAssemblies);

            // This can happen for scripts in packages that are not included in an .asmdef assembly
            // and they will therefore not be compiled.
            if (targetAssembly == null)
                return;

            dirtyState.AddDirtyTargetAssembly(targetAssembly);

            CheckIfCodeGenAssemblyIsDirty(assemblyFilename);
        }

        public void DirtyMovedScript(string oldPath, string newPath)
        {
            var assembly = GetTargetAssemblyDetails(newPath);
            if (assembly != null)
            {
                var assemblyFilename = assembly.Filename;
                DirtyScript(newPath, assemblyFilename);
            }

            var targetAssembly = EditorBuildRules.GetTargetAssemblyLinearSearch(oldPath, projectDirectory, customTargetAssemblies);

            // The target assembly might not exist any more.
            if (targetAssembly == null)
            {
                DirtyAllScripts();
            }
            else
            {
                dirtyState.AddDirtyTargetAssembly(targetAssembly);
                CheckIfCodeGenAssemblyIsDirty(targetAssembly.Filename);
            }
        }

        public void DirtyChangedAssemblyDefinition(string assemblyName)
        {
            if (!filePathToCustomScriptAssemblies.TryGetValue(assemblyName, out var customScriptAssembly))
            {
                DirtyAllScripts();
                return;
            }

            TargetAssembly customTargetAssembly = GetCustomTargetAssemblyFromName(customScriptAssembly.Name);
            dirtyState.AddDirtyTargetAssembly(customTargetAssembly);
        }

        public void DirtyRemovedScript(string path)
        {
            allScripts.Remove(path);

            var targetAssembly = EditorBuildRules.GetTargetAssemblyLinearSearch(path, projectDirectory, customTargetAssemblies);

            // The target assembly might not exist any more.
            if (targetAssembly == null)
            {
                DirtyAllScripts();
            }
            else
            {
                dirtyState.AddDirtyTargetAssembly(targetAssembly);
                CheckIfCodeGenAssemblyIsDirty(targetAssembly.Filename);

                // Add to changedAssemblies in case we delete the last script of an assembly and then do not get OnCompilationFinished callback

                AddChangedAssembly(targetAssembly.Filename, EditorScriptCompilationOptions.BuildingForEditor);
            }
        }

        void CheckIfCodeGenAssemblyIsDirty(string assemblyName)
        {
            if (dirtyState.AreAllTargetAssembliesDirty)
                return;

            // Mark all assemblies are dirty when a codgen assembly is dirty,
            // so both codgen and non-codegen assemblies get recompiled.
            if (UnityCodeGenHelpers.IsCodeGen(assemblyName))
            {
                DirtyAllScripts();
                return;
            }

            // Mark all assemblies are dirty when any codegen assembly does not exist,
            // so both codgen and non-codegen assemblies get recompiled.
            foreach (var entry in CustomTargetAssemblies)
            {
                var customTargetAssembly = entry.Value;

                if (!UnityCodeGenHelpers.IsCodeGen(customTargetAssembly.Filename))
                    continue;

                if (!AssetPath.Exists(customTargetAssembly.FullPath(outputDirectory)))
                {
                    DirtyAllScripts();
                    return;
                }
            }
        }

        public void DirtyPrecompiledAssemblies(string[] paths)
        {
            if (paths.Length == 0)
                return;

            PrecompiledAssemblyProvider.Dirty();

            // GetPrecompiledAssemblies once to avoid multiple times clearing and logging potential setup errors
            PrecompiledAssembly[] precompiledAssemblies = null;
            try
            {
                precompiledAssemblies = GetPrecompiledAssembliesWithSetupErrorsTracking(
                    true, EditorUserBuildSettings.activeBuildTargetGroup, EditorUserBuildSettings.activeBuildTarget);
            }
            catch (PrecompiledAssemblyException)
            {
                // If we have precompiled assembly setup errors, they have been logged and we can proceed safely
            }
            finally
            {
                foreach (var path in paths)
                {
                    DirtyPrecompiledAssembly(path, precompiledAssemblies);
                }
                dirtyPrecompiledAssembly?.Invoke(paths);
            }
        }

        private void DirtyPrecompiledAssembly(string path, PrecompiledAssembly[] precompiledAssemblies)
        {
            var filename = AssetPath.GetFileName(path);

            AddChangedAssembly(filename, EditorScriptCompilationOptions.BuildingForEditor);

            PrecompiledAssembly? precompiledAssembly = null;
            if (precompiledAssemblies != null)
                foreach (var assembly in precompiledAssemblies)
                {
                    if (AssetPath.GetFileName(assembly.Path) == filename)
                    {
                        precompiledAssembly = assembly;
                        break;
                    }
                }

            if (!precompiledAssembly.HasValue)
            {
                DirtyAllPrecompiledAssemblies();
                return;
            }

            var explicitlyReferenced = (precompiledAssembly.Value.Flags & AssemblyFlags.ExplicitlyReferenced) == AssemblyFlags.ExplicitlyReferenced;

            // If the precompiled assembly is not explicitly referenced, then
            // all scripts reference it and all scripts must be recompiled.
            if (!explicitlyReferenced)
            {
                DirtyAllPrecompiledAssemblies();
            }
            else
            {
                dirtyState.DirtyPrecompiledAssembly(filename);
            }
        }

        private void DirtyAllPrecompiledAssemblies()
        {
            dirtyState.DirtyAllPrecompiledAssemblies();
            DirtyAllScripts();
        }

        public void RecompileAllScriptsOnNextTick()
        {
            recompileAllScriptsOnNextTick = true;
        }

        public bool WillRecompileAllScriptsOnNextTick()
        {
            return recompileAllScriptsOnNextTick;
        }

        public void ClearDirtyScripts()
        {
            dirtyState.ClearAll();
        }

        public void RunScriptUpdaterOnAssembly(string assemblyFilename)
        {
            runScriptUpdaterAssemblies.Add(assemblyFilename);
        }

        public void SetAllUnityAssemblies(PrecompiledAssembly[] unityAssemblies)
        {
            this.unityAssemblies = unityAssemblies;
        }

        // Burst package depends on this method, so we can't remove it.
        public void SetCompileScriptsOutputDirectory(string directory)
        {
            outputDirectory = directory;
        }

        public void SetAssembliesOutputDirectories(string directory, string editorDirectory)
        {
            outputDirectory = directory;
            outputDirectoryEditor = editorDirectory;
        }

        public string GetCompileScriptsOutputDirectory()
        {
            if (string.IsNullOrEmpty(outputDirectory))
                throw new Exception("Must set an output directory through SetCompileScriptsOutputDirectory before compiling");
            return outputDirectory;
        }

        public string GetEditorAssembliesOutputDirectory()
        {
            if (string.IsNullOrEmpty(outputDirectoryEditor))
                throw new Exception("Must set output directories through SetAssembliesOutputDirectories before compiling");
            return outputDirectoryEditor;
        }

        private Dictionary<string, PrecompiledAssembly> GetPrecompiledAssembliesDictionaryWithSetupErrorsTracking(bool isEditor, BuildTargetGroup buildTargetGroup, BuildTarget buildTarget)
        {
            Dictionary<string, PrecompiledAssembly> precompiledAssemblies;
            CompilationSetupErrorsTracker.ClearCompilationSetupErrors(CompilationSetupErrors.PrecompiledAssemblyError); // this will also remove the console errors associated with the setup error flags set in the past
            try
            {
                precompiledAssemblies = PrecompiledAssemblyProvider.GetPrecompiledAssembliesDictionary(isEditor, buildTargetGroup, buildTarget);
            }
            catch (PrecompiledAssemblyException exception)
            {
                CompilationSetupErrorsTracker.ProcessPrecompiledAssemblyException(exception);
                throw;
            }
            return precompiledAssemblies;
        }

        public PrecompiledAssembly[] GetPrecompiledAssembliesWithSetupErrorsTracking(bool isEditor, BuildTargetGroup buildTargetGroup, BuildTarget buildTarget)
        {
            return GetPrecompiledAssembliesDictionaryWithSetupErrorsTracking(isEditor, buildTargetGroup, buildTarget)?.Values.ToArray();
        }

        //Used by the TestRunner package.
        internal PrecompiledAssembly[] GetAllPrecompiledAssemblies()
        {
            return PrecompiledAssemblyProvider.GetPrecompiledAssemblies(true, EditorUserBuildSettings.activeBuildTargetGroup, EditorUserBuildSettings.activeBuildTarget);
        }

        public void GetAssemblyDefinitionReferencesWithMissingAssemblies(out List<CustomScriptAssemblyReference> referencesWithMissingAssemblies)
        {
            var nameLookup = customScriptAssemblies.ToDictionary(x => x.Name);
            referencesWithMissingAssemblies = new List<CustomScriptAssemblyReference>();
            foreach (var asmref in customScriptAssemblyReferences)
            {
                if (!nameLookup.ContainsKey(asmref.Reference))
                {
                    referencesWithMissingAssemblies.Add(asmref);
                }
            }
        }

        public TargetAssembly GetCustomTargetAssemblyFromName(string name)
        {
            TargetAssembly targetAssembly;

            if (name.EndsWith(".dll", StringComparison.Ordinal))
            {
                customTargetAssemblies.TryGetValue(name, out targetAssembly);
            }
            else
            {
                customTargetAssemblies.TryGetValue(name + ".dll", out targetAssembly);
            }

            if (targetAssembly == null)
            {
                throw new ArgumentException("Assembly not found", name);
            }

            return targetAssembly;
        }

        public TargetAssemblyInfo[] GetAllCompiledAndResolvedTargetAssemblies(
            EditorScriptCompilationOptions options,
            BuildTarget buildTarget,
            out CustomScriptAssemblyAndReference[] assembliesWithMissingReference)
        {
            var allTargetAssemblies = GetTargetAssemblies();
            var targetAssemblyCompiledPaths = new Dictionary<TargetAssembly, string>();

            foreach (var assembly in allTargetAssemblies)
            {
                var path = assembly.FullPath(outputDirectory);

                // Collect all assemblies that have been compiled (exist on file system)
                if (File.Exists(path))
                    targetAssemblyCompiledPaths.Add(assembly, path);
            }

            bool removed;

            var removedCustomAssemblies = new List<CustomScriptAssemblyAndReference>();
            var assembliesWithScripts = GetTargetAssembliesWithScriptsHashSet(options);

            do
            {
                removed = false;

                if (targetAssemblyCompiledPaths.Count > 0)
                {
                    foreach (var assembly in allTargetAssemblies)
                    {
                        if (!targetAssemblyCompiledPaths.ContainsKey(assembly))
                            continue;

                        // Check for each compiled assembly that all it's references
                        // have also been compiled. If not, remove it from the list
                        // of compiled assemblies.
                        foreach (var reference in assembly.References)
                        {
                            // Don't check references that are not compatible with the current build target,
                            // as those assemblies have not been compiled.
                            if (!EditorBuildRules.IsCompatibleWithPlatformAndDefines(reference, buildTarget, options))
                                continue;

                            if (!assembliesWithScripts.Contains(reference))
                            {
                                continue;
                            }

                            if (assembly.Type == TargetAssemblyType.Custom && !targetAssemblyCompiledPaths.ContainsKey(reference))
                            {
                                targetAssemblyCompiledPaths.Remove(assembly);

                                var customScriptAssembly = FindCustomTargetAssemblyFromTargetAssembly(assembly);
                                var customScriptAssemblyReference = FindCustomTargetAssemblyFromTargetAssembly(reference);

                                removedCustomAssemblies.Add(new CustomScriptAssemblyAndReference { Assembly = customScriptAssembly, Reference = customScriptAssemblyReference });
                                removed = true;
                                break;
                            }
                        }
                    }
                }
            }
            while (removed);

            var count = targetAssemblyCompiledPaths.Count;
            var targetAssemblies = new TargetAssemblyInfo[count];
            int index = 0;

            foreach (var entry in targetAssemblyCompiledPaths)
            {
                var assembly = entry.Key;
                targetAssemblies[index++] = ToTargetAssemblyInfo(assembly);
            }

            assembliesWithMissingReference = removedCustomAssemblies.ToArray();
            return targetAssemblies;
        }

        public string[] GetCompiledAssemblyGraph(string assemblyName)
        {
            if (assemblyName == null)
                throw new ArgumentNullException("assemblyName");

            if (compilationTask == null)
                throw new InvalidOperationException("Cannot call GetCompiledAssemblyGraph without having an active CompilationTask");

            var compiledScriptAssemblies = compilationTask.CodeGenAssemblies.Concat(compilationTask.ScriptAssemblies).ToArray();

            assemblyName = AssetPath.GetAssemblyNameWithoutExtension(assemblyName);
            ScriptAssembly scriptAssembly = compiledScriptAssemblies.SingleOrDefault(a => AssetPath.GetAssemblyNameWithoutExtension(a.Filename) == assemblyName);

            if (scriptAssembly == null)
                throw new ArgumentException($"Could not find assembly name '{assemblyName}' in GetCompiledAssemblyGraph.");

            // Build a dictionary with the set of compiled referencing assemblies for each compiled assembly.
            var referencingAssemblies = new Dictionary<ScriptAssembly, HashSet<ScriptAssembly>>();

            foreach (var compiledScriptAssembly in compiledScriptAssemblies)
            {
                foreach (var referenceAssembly in compiledScriptAssembly.ScriptAssemblyReferences)
                {
                    HashSet<ScriptAssembly> referencingScriptAssemblies;

                    if (!referencingAssemblies.TryGetValue(referenceAssembly, out referencingScriptAssemblies))
                    {
                        referencingScriptAssemblies = new HashSet<ScriptAssembly>();
                        referencingAssemblies[referenceAssembly] = referencingScriptAssemblies;
                    }

                    referencingScriptAssemblies.Add(compiledScriptAssembly);
                }
            }

            HashSet<ScriptAssembly> referencing;

            // If there are no referencing assemblies, just return the single assembly
            if (!referencingAssemblies.TryGetValue(scriptAssembly, out referencing))
            {
                return new[] { scriptAssembly.Filename };
            }

            // Find all direct and indirect referencing assemblies, e.g. the
            // entire graph of assemblies that would be recompiled if this assembly
            // was recompiled.
            HashSet<string> result = new HashSet<string>();

            result.Add(scriptAssembly.Filename);

            List<ScriptAssembly> visit = new List<ScriptAssembly>(referencing);

            while (visit.Count > 0)
            {
                int lastIndex = visit.Count - 1;
                var visitAssembly = visit[lastIndex];
                visit.RemoveAt(lastIndex);

                result.Add(visitAssembly.Filename);

                if (!referencingAssemblies.TryGetValue(visitAssembly, out referencing))
                {
                    continue;
                }

                foreach (var referencingAssembly in referencing)
                {
                    if (result.Contains(referencingAssembly.Filename))
                        continue;
                    visit.Add(referencingAssembly);
                }
            }

            return result.ToArray();
        }

        static CustomScriptAssembly LoadCustomScriptAssemblyFromJsonPath(string path, string guid)
        {
            var json = Utility.ReadTextAsset(path);

            try
            {
                var customScriptAssemblyData = CustomScriptAssemblyData.FromJson(json);
                return CustomScriptAssembly.FromCustomScriptAssemblyData(path, guid, customScriptAssemblyData);
            }
            catch (Exception e)
            {
                throw new Compilation.AssemblyDefinitionException(e.Message, path);
            }
        }

        static CustomScriptAssembly LoadCustomScriptAssemblyFromJson(string path, string json, string guid)
        {
            try
            {
                var customScriptAssemblyData = CustomScriptAssemblyData.FromJson(json);
                return CustomScriptAssembly.FromCustomScriptAssemblyData(path, guid, customScriptAssemblyData);
            }
            catch (Exception e)
            {
                throw new Compilation.AssemblyDefinitionException(e.Message, path);
            }
        }

        static CustomScriptAssemblyReference LoadCustomScriptAssemblyReferenceFromJsonPath(string path)
        {
            var json = Utility.ReadTextAsset(path);
            return LoadCustomScriptAssemblyReferenceFromJson(path, json);
        }

        static CustomScriptAssemblyReference LoadCustomScriptAssemblyReferenceFromJson(string path, string json)
        {
            try
            {
                var customScriptAssemblyRefData = CustomScriptAssemblyReferenceData.FromJson(json);
                return CustomScriptAssemblyReference.FromCustomScriptAssemblyReferenceData(path, customScriptAssemblyRefData);
            }
            catch (Exception e)
            {
                throw new Compilation.AssemblyDefinitionException(e.Message, path);
            }
        }

        string[] CustomTargetAssembliesToFilePaths(IEnumerable<TargetAssembly> targetAssemblies)
        {
            var customAssemblies = targetAssemblies.Select(a => FindCustomTargetAssemblyFromTargetAssembly(a));
            var filePaths = customAssemblies.Select(a => a.FilePath).ToArray();
            return filePaths;
        }

        string CustomTargetAssemblyToFilePath(TargetAssembly targetAssembly)
        {
            return FindCustomTargetAssemblyFromTargetAssembly(targetAssembly).FilePath;
        }

        public struct CheckCyclicAssemblyReferencesFunctions
        {
            public Func<TargetAssembly, string> ToFilePathFunc;
            public Func<IEnumerable<TargetAssembly>, string[]> ToFilePathsFunc;
        }

        static void CheckCyclicAssemblyReferencesDFS(TargetAssembly visitAssembly,
            HashSet<TargetAssembly> visited,
            HashSet<TargetAssembly> recursion,
            CheckCyclicAssemblyReferencesFunctions functions)
        {
            visited.Add(visitAssembly);
            recursion.Add(visitAssembly);

            foreach (var reference in visitAssembly.References)
            {
                if (reference.Filename == visitAssembly.Filename)
                {
                    throw new Compilation.AssemblyDefinitionException("Assembly contains a references to itself",
                        AssemblyDefinitionErrorType.CyclicReferences, functions.ToFilePathFunc(visitAssembly));
                }

                if (recursion.Contains(reference))
                {
                    throw new Compilation.AssemblyDefinitionException("Assembly with cyclic references detected",
                        AssemblyDefinitionErrorType.CyclicReferences, functions.ToFilePathsFunc(recursion));
                }

                if (!visited.Contains(reference))
                {
                    CheckCyclicAssemblyReferencesDFS(reference,
                        visited,
                        recursion,
                        functions);
                }
            }

            recursion.Remove(visitAssembly);
        }

        public static void CheckCyclicAssemblyReferences(IDictionary<string, TargetAssembly> customTargetAssemblies,
            CheckCyclicAssemblyReferencesFunctions functions)
        {
            if (customTargetAssemblies == null || customTargetAssemblies.Count < 1)
                return;

            var visited = new HashSet<TargetAssembly>();

            foreach (var entry in customTargetAssemblies)
            {
                var assembly = entry.Value;
                if (!visited.Contains(assembly))
                {
                    var recursion = new HashSet<TargetAssembly>();

                    CheckCyclicAssemblyReferencesDFS(assembly,
                        visited,
                        recursion,
                        functions);
                }
            }
        }

        void CheckCyclicAssemblyReferences()
        {
            try
            {
                CheckCyclicAssemblyReferencesFunctions functions;

                functions.ToFilePathFunc = CustomTargetAssemblyToFilePath;
                functions.ToFilePathsFunc = CustomTargetAssembliesToFilePaths;

                CheckCyclicAssemblyReferences(customTargetAssemblies, functions);
            }
            catch (AssemblyDefinitionException e)
            {
                if (e.errorType == AssemblyDefinitionErrorType.CyclicReferences)
                    CompilationSetupErrorsTracker.SetCompilationSetupErrors(CompilationSetupErrors.CyclicReferences);
                throw e;
            }
        }

        public static Exception[] UpdateCustomScriptAssemblies(CustomScriptAssembly[] customScriptAssemblies,
            List<CustomScriptAssemblyReference> customScriptAssemblyReferences,
            AssetPathMetaData[] assetPathsMetaData, ResponseFileProvider responseFileProvider)
        {
            var asmrefLookup = customScriptAssemblyReferences.ToLookup(x => x.Reference);

            // Add AdditionalPrefixes
            foreach (var assembly in customScriptAssemblies)
            {
                var foundAsmRefs = asmrefLookup[assembly.Name];

                // Assign the found references or null. We need to assign null so as not to hold onto references that may have been removed/changed.
                assembly.AdditionalPrefixes = foundAsmRefs.Any() ? foundAsmRefs.Select(ar => ar.PathPrefix).ToArray() : null;
            }

            UpdateCustomTargetAssembliesResponseFileData(customScriptAssemblies, responseFileProvider);
            var exceptions = UpdateCustomTargetAssembliesAssetPathsMetaData(customScriptAssemblies, assetPathsMetaData);
            return exceptions.ToArray();
        }

        static void UpdateCustomTargetAssembliesResponseFileData(CustomScriptAssembly[] customScriptAssemblies, ResponseFileProvider responseFileProvider)
        {
            foreach (var assembly in customScriptAssemblies)
            {
                string rspFile = responseFileProvider.Get(assembly.PathPrefix)
                    .SingleOrDefault();
                if (!string.IsNullOrEmpty(rspFile))
                {
                    var responseFileContent = MicrosoftResponseFileParser.GetResponseFileContent(Directory.GetParent(Application.dataPath).FullName, rspFile);
                    var compilerOptions = MicrosoftResponseFileParser.GetCompilerOptions(responseFileContent);
                    assembly.ResponseFileDefines = MicrosoftResponseFileParser.GetDefines(compilerOptions).ToArray();
                }
            }
        }

        static Exception[] UpdateCustomTargetAssembliesAssetPathsMetaData(CustomScriptAssembly[] customScriptAssemblies,
            AssetPathMetaData[] assetPathsMetaData, bool forceUpdate = false)
        {
            if (assetPathsMetaData == null)
            {
                return new Exception[0];
            }

            var exceptions = new List<Exception>();
            var assetMetaDataPaths = new string[assetPathsMetaData.Length];
            var lowerAssetMetaDataPaths = new string[assetPathsMetaData.Length];

            for (int i = 0; i < assetPathsMetaData.Length; ++i)
            {
                var assetPathMetaData = assetPathsMetaData[i];
                assetMetaDataPaths[i] = AssetPath.ReplaceSeparators(assetPathMetaData.DirectoryPath + AssetPath.Separator);
                lowerAssetMetaDataPaths[i] = Utility.FastToLower(assetMetaDataPaths[i]);
            }

            foreach (var assembly in customScriptAssemblies)
            {
                if (assembly.AssetPathMetaData != null && !forceUpdate)
                {
                    continue;
                }

                try
                {
                    for (int i = 0; i < assetMetaDataPaths.Length; ++i)
                    {
                        var path = assetMetaDataPaths[i];
                        var lowerPath = lowerAssetMetaDataPaths[i];

                        if (Utility.FastStartsWith(assembly.PathPrefix, path, lowerPath))
                        {
                            assembly.AssetPathMetaData = assetPathsMetaData[i];
                            break;
                        }
                    }
                }
                catch (Exception e)
                {
                    exceptions.Add(e);
                }
            }

            return exceptions.ToArray();
        }

        Exception[] UpdateCustomTargetAssemblies(bool forceUpdateAssetMetadata = false)
        {
            var exceptions = UpdateCustomScriptAssemblies(customScriptAssemblies, customScriptAssemblyReferences, m_AssetPathsMetaData, ResponseFileProvider);

            if (exceptions.Length > 0)
            {
                CompilationSetupErrorsTracker.SetCompilationSetupErrors(CompilationSetupErrors.LoadError);
            }

            customTargetAssemblies = EditorBuildRules.CreateTargetAssemblies(customScriptAssemblies);

            CompilationSetupErrorsTracker.ClearCompilationSetupErrors(CompilationSetupErrors.CyclicReferences);

            // Remap dirtyTargetAssemblies to new objects created due to
            // customTargetAssemblies being updated.
            UpdateDirtyTargetAssemblies();

            return exceptions;
        }

        void UpdateDirtyTargetAssemblies()
        {
            if (dirtyState.DirtyTargetAssemblies.Count == 0)
                return;

            var dirtyTargetAssemblyFilenames = new HashSet<string>();

            foreach (var targetAssembly in dirtyState.DirtyTargetAssemblies)
            {
                dirtyTargetAssemblyFilenames.Add(targetAssembly.Filename);
            }

            var newDirtyTargetAssemblies = new HashSet<TargetAssembly>();

            if (customTargetAssemblies != null)
            {
                foreach (var entry in customTargetAssemblies)
                {
                    var customTargetAssembly = entry.Value;
                    if (dirtyTargetAssemblyFilenames.Contains(customTargetAssembly.Filename))
                    {
                        newDirtyTargetAssemblies.Add(customTargetAssembly);
                    }
                }
            }

            var predefinedTargetAssemblies = EditorBuildRules.GetPredefinedTargetAssemblies();

            foreach (var predefinedTargetAssembly in predefinedTargetAssemblies)
            {
                if (dirtyState.DirtyTargetAssemblies.Contains(predefinedTargetAssembly))
                {
                    newDirtyTargetAssemblies.Add(predefinedTargetAssembly);
                }
            }

            dirtyState.DirtyTargetAssemblies = newDirtyTargetAssemblies;
        }

        public void SkipCustomScriptAssemblyGraphValidation(bool skipChecks)
        {
            skipCustomScriptAssemblyGraphValidation = skipChecks;
        }

        public void ClearCustomScriptAssemblies()
        {
            customScriptAssemblies = null;
            customScriptAssemblyReferences.Clear();
        }

        public Exception[] SetAllCustomScriptAssemblyReferenceJsons(string[] paths)
        {
            return SetAllCustomScriptAssemblyReferenceJsonsContents(paths, null);
        }

        public Exception[] SetAllCustomScriptAssemblyReferenceJsonsContents(string[] paths, string[] contents)
        {
            var assemblyRefs = new List<CustomScriptAssemblyReference>();
            var exceptions = new List<Exception>();

            // We only construct this lookup if it is required, which is when we are using guids instead of assembly names.
            Dictionary<string, CustomScriptAssembly> guidsToAssemblies = null;

            // To check if a path prefix is already being used we use a Dictionary where the key is the prefix and the value is the file path.
            var prefixToFilePathLookup = skipCustomScriptAssemblyGraphValidation ?
                null :
                customScriptAssemblies.GroupBy(x => x.PathPrefix).ToDictionary(x => x.First().PathPrefix, x => new List<string>() { x.First().FilePath }, StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < paths.Length; ++i)
            {
                var path = paths[i];

                CustomScriptAssemblyReference loadedCustomScriptAssemblyReference = null;

                try
                {
                    var fullPath = AssetPath.IsPathRooted(path) ? AssetPath.GetFullPath(path) : AssetPath.Combine(projectDirectory, path);

                    if (contents != null)
                    {
                        var jsonContents = contents[i];
                        loadedCustomScriptAssemblyReference = LoadCustomScriptAssemblyReferenceFromJson(fullPath, jsonContents);
                    }
                    else
                    {
                        loadedCustomScriptAssemblyReference = LoadCustomScriptAssemblyReferenceFromJsonPath(fullPath);
                    }

                    if (!skipCustomScriptAssemblyGraphValidation)
                    {
                        // Check both asmdef and asmref files.
                        List<string> duplicateFilePaths;
                        if (prefixToFilePathLookup.TryGetValue(loadedCustomScriptAssemblyReference.PathPrefix, out duplicateFilePaths))
                        {
                            var filePaths = new List<string>();
                            filePaths.Add(loadedCustomScriptAssemblyReference.FilePath);
                            filePaths.AddRange(duplicateFilePaths);

                            throw new Compilation.AssemblyDefinitionException(string.Format("Folder '{0}' contains multiple assembly definition files", loadedCustomScriptAssemblyReference.PathPrefix), filePaths.ToArray());
                        }
                    }

                    // Convert GUID references to assembly names
                    if (GUIDReference.IsGUIDReference(loadedCustomScriptAssemblyReference.Reference))
                    {
                        // Generate the guid to assembly lookup?
                        if (guidsToAssemblies == null)
                            guidsToAssemblies = customScriptAssemblies.ToDictionary(x => x.GUID);

                        var guid = Utility.FastToLower(GUIDReference.GUIDReferenceToGUID(loadedCustomScriptAssemblyReference.Reference));
                        CustomScriptAssembly foundAssembly;
                        if (guidsToAssemblies.TryGetValue(guid, out foundAssembly))
                        {
                            loadedCustomScriptAssemblyReference.Reference = foundAssembly.Name;
                        }
                    }
                }
                catch (Exception e)
                {
                    CompilationSetupErrorsTracker.SetCompilationSetupErrors(CompilationSetupErrors.LoadError);
                    exceptions.Add(e);
                }

                if (loadedCustomScriptAssemblyReference != null)
                {
                    assemblyRefs.Add(loadedCustomScriptAssemblyReference);

                    if (!skipCustomScriptAssemblyGraphValidation)
                    {
                        List<string> duplicateFilePaths;
                        if (!prefixToFilePathLookup.TryGetValue(loadedCustomScriptAssemblyReference.PathPrefix, out duplicateFilePaths))
                        {
                            duplicateFilePaths = new List<string>();
                            prefixToFilePathLookup[loadedCustomScriptAssemblyReference.PathPrefix] = duplicateFilePaths;
                        }

                        duplicateFilePaths.Add(loadedCustomScriptAssemblyReference.FilePath);
                    }
                }
            }

            customScriptAssemblyReferences = assemblyRefs;
            var updateCustomTargetAssembliesExceptions = UpdateCustomTargetAssemblies();
            exceptions.AddRange(updateCustomTargetAssembliesExceptions);
            return exceptions.ToArray();
        }

        public Exception[] SetAllCustomScriptAssemblyJsons(string[] paths, string[] guids)
        {
            return SetAllCustomScriptAssemblyJsonContents(paths, null, guids);
        }

        public Exception[] SetAllCustomScriptAssemblyJsonContents(string[] paths, string[] contents, string[] guids)
        {
            var assemblies = new List<CustomScriptAssembly>();
            var filePathToAssembly = new Dictionary<string, CustomScriptAssembly>();
            var assemblyLowercaseNamesLookup = new Dictionary<string, CustomScriptAssembly>();
            var exceptions = new List<Exception>();
            var guidsToAssemblies = new Dictionary<string, CustomScriptAssembly>();
            HashSet<string> predefinedAssemblyNames = null;

            // To check if a path prefix is already being used we use a Dictionary where the key is the prefix and the value is the file path.
            var prefixToFilePathLookup = customScriptAssemblyReferences.ToDictionary(x => x.PathPrefix, x => new List<string>() { x.FilePath }, StringComparer.OrdinalIgnoreCase);

            CompilationSetupErrorsTracker.ClearCompilationSetupErrors(CompilationSetupErrors.LoadError);

            // Load first to setup guidsToAssemblies dictionary and convert guids to assembly names
            // before checking for assembly reference errors, so errors emit assembly names instead of guids.
            for (var i = 0; i < paths.Length; ++i)
            {
                var path = paths[i];
                var guid = guids[i];

                CustomScriptAssembly loadedCustomScriptAssembly = null;
                string lowerCaseName = null;

                try
                {
                    var fullPath = AssetPath.IsPathRooted(path) ? AssetPath.GetFullPath(path) : AssetPath.Combine(projectDirectory, path);

                    if (contents != null)
                    {
                        var jsonContents = contents[i];
                        loadedCustomScriptAssembly = LoadCustomScriptAssemblyFromJson(fullPath, jsonContents, guid);
                    }
                    else
                    {
                        loadedCustomScriptAssembly = LoadCustomScriptAssemblyFromJsonPath(fullPath, guid);
                    }

                    if (loadedCustomScriptAssembly.References == null)
                        loadedCustomScriptAssembly.References = new string[0];

                    lowerCaseName = Utility.FastToLower(loadedCustomScriptAssembly.Name);
                    guidsToAssemblies[Utility.FastToLower(guid)] = loadedCustomScriptAssembly;

                    if (!skipCustomScriptAssemblyGraphValidation)
                    {
                        if (predefinedAssemblyNames == null)
                        {
                            predefinedAssemblyNames = new HashSet<string>(EditorBuildRules.PredefinedTargetAssemblyNames);
                            var net46 = MonoLibraryHelpers.GetSystemLibraryReferences(ApiCompatibilityLevel.NET_4_6, ScriptCompilers.CSharpSupportedLanguage).Select(Path.GetFileNameWithoutExtension);
                            var netstandard20 = MonoLibraryHelpers.GetSystemLibraryReferences(ApiCompatibilityLevel.NET_Standard_2_0, ScriptCompilers.CSharpSupportedLanguage).Select(Path.GetFileNameWithoutExtension);
                            predefinedAssemblyNames.UnionWith(net46);
                            predefinedAssemblyNames.UnionWith(netstandard20);
                        }

                        if (predefinedAssemblyNames.Contains(loadedCustomScriptAssembly.Name))
                        {
                            throw new Compilation.AssemblyDefinitionException(
                                $"Assembly cannot be have reserved name '{loadedCustomScriptAssembly.Name}'",
                                loadedCustomScriptAssembly.FilePath);
                        }

                        CustomScriptAssembly duplicate;
                        if (assemblyLowercaseNamesLookup.TryGetValue(lowerCaseName, out duplicate))
                        {
                            var filePaths = new string[]
                            {
                                loadedCustomScriptAssembly.FilePath,
                                duplicate.FilePath
                            };
                            var errorMsg = string.Format("Assembly with name '{0}' already exists", loadedCustomScriptAssembly.Name);
                            loadedCustomScriptAssembly = null; // Set to null to prevent it being added.
                            throw new Compilation.AssemblyDefinitionException(errorMsg, filePaths);
                        }

                        // Check both asmdef and asmref files.
                        List<string> duplicateFilePaths;
                        if (prefixToFilePathLookup.TryGetValue(loadedCustomScriptAssembly.PathPrefix, out duplicateFilePaths))
                        {
                            var filePaths = new List<string>();
                            filePaths.Add(loadedCustomScriptAssembly.FilePath);
                            filePaths.AddRange(duplicateFilePaths);

                            throw new Compilation.AssemblyDefinitionException(
                                string.Format("Folder '{0}' contains multiple assembly definition files",
                                    loadedCustomScriptAssembly.PathPrefix), filePaths.ToArray());
                        }
                    }
                }
                catch (Exception e)
                {
                    CompilationSetupErrorsTracker.SetCompilationSetupErrors(CompilationSetupErrors.LoadError);
                    exceptions.Add(e);
                }

                if (loadedCustomScriptAssembly != null)
                {
                    if (loadedCustomScriptAssembly.References == null)
                        loadedCustomScriptAssembly.References = new string[0];

                    if (!skipCustomScriptAssemblyGraphValidation || !assemblyLowercaseNamesLookup.ContainsKey(lowerCaseName))
                    {
                        assemblyLowercaseNamesLookup[lowerCaseName] = loadedCustomScriptAssembly;
                        assemblies.Add(loadedCustomScriptAssembly);
                        filePathToAssembly.Add(loadedCustomScriptAssembly.FilePath, loadedCustomScriptAssembly);

                        List<string> duplicateFilePaths;
                        if (!prefixToFilePathLookup.TryGetValue(loadedCustomScriptAssembly.PathPrefix, out duplicateFilePaths))
                        {
                            duplicateFilePaths = new List<string>();
                            prefixToFilePathLookup[loadedCustomScriptAssembly.PathPrefix] = duplicateFilePaths;
                        }
                        duplicateFilePaths.Add(loadedCustomScriptAssembly.FilePath);
                    }
                }
            }

            // Convert GUID references to assembly names
            foreach (var assembly in assemblies)
            {
                for (int i = 0; i < assembly.References.Length; ++i)
                {
                    var reference = assembly.References[i];

                    if (!GUIDReference.IsGUIDReference(reference))
                    {
                        continue;
                    }

                    var guid = Utility.FastToLower(GUIDReference.GUIDReferenceToGUID(reference));

                    CustomScriptAssembly referenceAssembly;

                    if (guidsToAssemblies.TryGetValue(guid, out referenceAssembly))
                    {
                        reference = referenceAssembly.Name;
                    }
                    else
                    {
                        reference = string.Empty;
                    }

                    assembly.References[i] = reference;
                }
            }

            // Check loaded assemblies for assembly reference errors after all GUID references have been
            // converted to names.
            if (!skipCustomScriptAssemblyGraphValidation)
            {
                foreach (var loadedCustomScriptAssembly in assemblies)
                {
                    try
                    {
                        var references = loadedCustomScriptAssembly.References.Where(r => !string.IsNullOrEmpty(r));

                        if (references.Count() != references.Distinct().Count())
                        {
                            var duplicateRefs = references.GroupBy(r => r).Where(g => g.Count() > 1).Select(g => g.Key)
                                .ToArray();
                            var duplicateRefsString = string.Join(",", duplicateRefs);

                            throw new Compilation.AssemblyDefinitionException(string.Format(
                                "Assembly has duplicate references: {0}",
                                duplicateRefsString),
                                loadedCustomScriptAssembly.FilePath);
                        }
                    }
                    catch (Exception e)
                    {
                        CompilationSetupErrorsTracker.SetCompilationSetupErrors(CompilationSetupErrors.LoadError);
                        exceptions.Add(e);
                    }
                }
            }

            customScriptAssemblies = assemblies.ToArray();
            filePathToCustomScriptAssemblies = filePathToAssembly;

            var updateCustomTargetAssembliesExceptions = UpdateCustomTargetAssemblies();
            exceptions.AddRange(updateCustomTargetAssembliesExceptions);

            return exceptions.ToArray();
        }

        public bool IsPathInPackageDirectory(string path)
        {
            if (m_AssetPathsMetaData == null)
                return false;
            return m_AssetPathsMetaData.Any(p => path.StartsWith(p.DirectoryPath, StringComparison.OrdinalIgnoreCase));
        }

        public void DeleteUnusedAssemblies()
        {
            ScriptAssemblySettings settings = CreateEditorScriptAssemblySettings(EditorScriptCompilationOptions.BuildingForEditor);
            DeleteUnusedAssemblies(settings, fileIOProvider, directoryIOProvider);
        }

        // Delete all .dll's that aren't used anymore
        public void DeleteUnusedAssemblies(ScriptAssemblySettings settings, IFileIO fileIO , IDirectoryIO directoryIO)
        {
            string fullEditorAssemblyPath = AssetPath.Combine(projectDirectory, GetCompileScriptsOutputDirectory());

            if (!settings.BuildingForEditor || !directoryIO.Exists(fullEditorAssemblyPath))
            {
                // This is called in GetTargetAssembliesWithScripts and is required for compilation to
                // be set up correctly. Since we early out here, we need to call this here.
                UpdateAllTargetAssemblyDefines(customTargetAssemblies, EditorBuildRules.GetPredefinedTargetAssemblies(), m_VersionMetaDatas, settings);
                return;
            }

            //This will also update all the defines on the TargetAssembly
            //This is needed as long as TargetAssemblies has Defines state on them.
            var targetAssemblies = GetTargetAssembliesWithScripts(settings);

            var deleteFiles = directoryIO.GetFiles(fullEditorAssemblyPath).Select(f => AssetPath.ReplaceSeparators(f)).ToList();
            string timestampPath = GetAssemblyTimestampPath(GetCompileScriptsOutputDirectory());
            deleteFiles.Remove(AssetPath.Combine(projectDirectory, timestampPath));

            foreach (var assembly in targetAssemblies)
            {
                string path = AssetPath.Combine(fullEditorAssemblyPath, assembly.Name);

                deleteFiles.Remove(path);
                deleteFiles.Remove(MDBPath(path));
                deleteFiles.Remove(PDBPath(path));
                unusedAssembly?.Invoke(assembly.Name);
            }

            foreach (var path in deleteFiles)
                DeleteFile(path, fileIO: fileIO);
        }

        public void CleanScriptAssemblies()
        {
            string fullEditorAssemblyPath = AssetPath.Combine(projectDirectory, GetCompileScriptsOutputDirectory());

            if (!Directory.Exists(fullEditorAssemblyPath))
                return;

            foreach (var path in Directory.GetFiles(fullEditorAssemblyPath))
                DeleteFile(path, fileIOProvider);
        }

        static void DeleteFile(string path, IFileIO fileIO, DeleteFileOptions fileOptions = DeleteFileOptions.LogError)
        {
            try
            {
                fileIO.Delete(path);
            }
            catch (Exception)
            {
                if (fileOptions == DeleteFileOptions.LogError)
                    UnityEngine.Debug.LogErrorFormat("Could not delete file '{0}'\n", path);
            }
        }

        static bool MoveOrReplaceFile(string sourcePath, string destinationPath, out string errorMessage, IFileIO fileIO)
        {
            bool fileMoved = false;
            errorMessage = string.Empty;

            // check existence first to avoid a first-chance file-exists exception
            if (!File.Exists(destinationPath))
            {
                try
                {
                    fileIO.Move(sourcePath, destinationPath);
                    fileMoved = true;
                }
                catch (IOException e)
                {
                    errorMessage = e.Message;
                }
            }

            if (!fileMoved)
            {
                var backupFile = destinationPath + ".bak";
                DeleteFile(backupFile, fileIO, DeleteFileOptions.NoLogError); // Delete any previous backup files.

                try
                {
                    File.Replace(sourcePath, destinationPath, backupFile, true);
                    fileMoved = true;
                }
                catch (IOException e)
                {
                    errorMessage = e.Message;
                }

                // Try to delete backup file. Does not need to exist
                // We will eventually delete the file in DeleteUnusedAssemblies.
                DeleteFile(backupFile, fileIO, DeleteFileOptions.NoLogError);
            }
            return fileMoved;
        }

        static string PDBPath(string dllPath)
        {
            return dllPath.Replace(".dll", ".pdb");
        }

        static string MDBPath(string dllPath)
        {
            return dllPath + ".mdb";
        }

        static bool CopyAssembly(string sourcePath, string destinationPath, IFileIO fileIO, out string errorMessage)
        {
            if (!MoveOrReplaceFile(sourcePath, destinationPath, out errorMessage, fileIO))
                return false;

            string sourceMdb = MDBPath(sourcePath);
            string destinationMdb = MDBPath(destinationPath);

            if (File.Exists(sourceMdb))
                MoveOrReplaceFile(sourceMdb, destinationMdb, out errorMessage, fileIO);
            else if (File.Exists(destinationMdb))
                DeleteFile(destinationMdb, fileIO);

            string combinedErrorMessage = errorMessage;

            string sourcePdb = PDBPath(sourcePath);
            string destinationPdb = PDBPath(destinationPath);

            if (File.Exists(sourcePdb))
                MoveOrReplaceFile(sourcePdb, destinationPdb, out errorMessage, fileIO);
            else if (File.Exists(destinationPdb))
                DeleteFile(destinationPdb, fileIO);

            combinedErrorMessage += $"\t{errorMessage}";
            errorMessage = combinedErrorMessage;

            return true;
        }

        public CustomScriptAssembly FindCustomScriptAssemblyFromAssemblyName(string assemblyName)
        {
            assemblyName = AssetPath.GetAssemblyNameWithoutExtension(assemblyName);

            if (customScriptAssemblies != null)
            {
                var result = customScriptAssemblies.FirstOrDefault(a => a.Name == assemblyName);
                if (result != null)
                    return result;
            }

            var exceptionMessage = "Cannot find CustomScriptAssembly with name '" + assemblyName + "'.";

            if (customScriptAssemblies == null)
            {
                exceptionMessage += " customScriptAssemblies is null.";
            }
            else
            {
                var assemblyNames = customScriptAssemblies.Select(a => a.Name).ToArray();
                var assemblyNamesString = string.Join(", ", assemblyNames);
                exceptionMessage += " Assembly names: " + assemblyNamesString;
            }

            throw new InvalidOperationException(exceptionMessage);
        }

        internal CustomScriptAssembly FindCustomScriptAssemblyFromScriptPath(string scriptPath)
        {
            var customTargetAssembly = EditorBuildRules.GetCustomTargetAssembly(scriptPath, projectDirectory, customTargetAssemblies);
            var customScriptAssembly = customTargetAssembly != null ? FindCustomScriptAssemblyFromAssemblyName(customTargetAssembly.Filename) : null;

            return customScriptAssembly;
        }

        internal CustomScriptAssembly FindCustomTargetAssemblyFromTargetAssembly(TargetAssembly assembly)
        {
            var assemblyName = AssetPath.GetAssemblyNameWithoutExtension(assembly.Filename);
            return FindCustomScriptAssemblyFromAssemblyName(assemblyName);
        }

        public CustomScriptAssembly FindCustomScriptAssemblyFromAssemblyReference(string reference)
        {
            if (!GUIDReference.IsGUIDReference(reference))
            {
                return FindCustomScriptAssemblyFromAssemblyName(reference);
            }

            if (customScriptAssemblies != null)
            {
                var guid = GUIDReference.GUIDReferenceToGUID(reference);
                var result = customScriptAssemblies.FirstOrDefault(a => string.Equals(a.GUID, guid, StringComparison.OrdinalIgnoreCase));

                if (result != null)
                    return result;
            }

            throw new InvalidOperationException($"Cannot find CustomScriptAssembly with reference '{reference}'");
        }

        private void CleanUpAfterCompilationCompleted()
        {
            dirtyState.IsDirty = false;
            dirtyState.ClearAll();
            compilationTask = null;
        }

        public CompileStatus CompileScripts(
            EditorScriptCompilationOptions editorScriptCompilationOptions,
            BuildTargetGroup platformGroup,
            BuildTarget platform,
            string[] extraScriptingDefines,
            CompilationTaskOptions compilationTaskOptions = CompilationTaskOptions.StopOnFirstError)
        {
            IsRunningRoslynAnalysisSynchronously =
                (editorScriptCompilationOptions & EditorScriptCompilationOptions.BuildingWithRoslynAnalysis) != 0 && PlayerSettings.EnableRoslynAnalyzers;

            var scriptAssemblySettings = CreateScriptAssemblySettings(platformGroup, platform, editorScriptCompilationOptions, extraScriptingDefines);

            TargetAssembly[] notCompiledTargetAssemblies = null;
            string[] notCompiledScripts = null;
            CompileStatus compilationResult = CompileStatus.Idle;
            try
            {
                compilationResult = CompileScriptsWithSettings(scriptAssemblySettings, EditorTempPath, ref notCompiledTargetAssemblies, ref notCompiledScripts, compilationTaskOptions);
            }
            catch (Exception)
            {
                CleanUpAfterCompilationCompleted();
                throw;
            }
            finally
            {
                if (notCompiledTargetAssemblies != null)
                {
                    foreach (var targetAssembly in notCompiledTargetAssemblies)
                    {
                        var customScriptAssembly = customScriptAssemblies.Single(a => a.Name == AssetPath.GetAssemblyNameWithoutExtension(targetAssembly.Filename));

                        var filePath = customScriptAssembly.FilePath;

                        if (filePath.StartsWith(projectDirectory, StringComparison.Ordinal))
                            filePath = filePath.Substring(projectDirectory.Length);

                        UnityEngine.Debug.LogWarning(string.Format("Script assembly '{0}' has not been compiled. Folder containing assembly definition file '{1}' contains script files for different script languages. Folder must only contain script files for one script language.", targetAssembly.Filename, filePath));
                    }
                }

                if (notCompiledScripts != null)
                {
                    Array.Sort(notCompiledScripts);

                    foreach (var script in notCompiledScripts)
                    {
                        UnityEngine.Debug.LogWarning(string.Format("Script '{0}' will not be compiled because it exists outside the Assets folder and does not to belong to any assembly definition file.", script));
                    }
                }
            }
            return compilationResult;
        }

        private static TargetAssembly[] GetPredefinedAssemblyReferences(IDictionary<string, TargetAssembly> targetAssemblies)
        {
            var targetAssembliesResult = (targetAssemblies.Values ?? Enumerable.Empty<TargetAssembly>())
                .Where(x => (x.Flags & AssemblyFlags.ExplicitlyReferenced) == AssemblyFlags.None)
                .ToArray();
            return targetAssembliesResult;
        }

        internal CompileStatus CompileScriptsWithSettings(
            ScriptAssemblySettings scriptAssemblySettings,
            string tempBuildDirectory,
            ref TargetAssembly[] notCompiledTargetAssemblies,
            ref string[] notCompiledScripts,
            CompilationTaskOptions compilationTaskOptions = CompilationTaskOptions.StopOnFirstError)
        {
            DeleteUnusedAssemblies(scriptAssemblySettings, fileIOProvider, directoryIOProvider);

            if (!DoesProjectFolderHaveAnyDirtyScripts() &&
                !ArePrecompiledAssembliesDirty() &&
                runScriptUpdaterAssemblies.Count == 0)
                return CompileStatus.Idle;

            IsRunningRoslynAnalysisSynchronously =
                PlayerSettings.EnableRoslynAnalyzers &&
                (scriptAssemblySettings.CompilationOptions & EditorScriptCompilationOptions.BuildingWithRoslynAnalysis) != 0;

            Dictionary<string, PrecompiledAssembly> precompiledAssemblies = null;
            try
            {
                precompiledAssemblies = GetPrecompiledAssembliesDictionaryWithSetupErrorsTracking(
                    scriptAssemblySettings.BuildingForEditor, scriptAssemblySettings.BuildTargetGroup, scriptAssemblySettings.BuildTarget);
            }
            catch (PrecompiledAssemblyException)
            {
                CleanUpAfterCompilationCompleted();
                return CompileStatus.Idle;
            }

            var assemblies = new EditorBuildRules.CompilationAssemblies
            {
                UnityAssemblies = unityAssemblies,
                PrecompiledAssemblies = precompiledAssemblies,
                RoslynAnalyzerDllPaths = PrecompiledAssemblyProvider.GetRoslynAnalyzerPaths(),
                CustomTargetAssemblies = customTargetAssemblies,
                PredefinedAssembliesCustomTargetReferences = GetPredefinedAssemblyReferences(customTargetAssemblies),
                EditorAssemblyReferences = ModuleUtils.GetAdditionalReferencesForUserScripts(),
            };

            var args = new EditorBuildRules.GenerateChangedScriptAssembliesArgs
            {
                AllSourceFiles = allScripts,
                DirtyTargetAssemblies = dirtyState.DirtyTargetAssemblies,
                DirtyPrecompiledAssemblies = dirtyState.DirtyPrecompiledAssemblies,
                ProjectDirectory = projectDirectory,
                Settings = scriptAssemblySettings,
                Assemblies = assemblies,
                RunUpdaterAssemblies = runScriptUpdaterAssemblies
            };

            ScriptAssembly[] scriptAssemblies = EditorBuildRules.GenerateChangedScriptAssemblies(args);

            foreach (var customTargetAssembly in args.NoScriptsCustomTargetAssemblies)
            {
                var customScriptAssembly = FindCustomTargetAssemblyFromTargetAssembly(customTargetAssembly);
                UnityEngine.Debug.LogWarningFormat("Assembly for Assembly Definition File '{0}' will not be compiled, because it has no scripts associated with it.", customScriptAssembly.FilePath);
            }

            notCompiledTargetAssemblies = args.NotCompiledTargetAssemblies.ToArray();
            notCompiledScripts = args.NotCompiledScripts.ToArray();

            // If only the last script of an assembly is removed, then scriptAssemblies will
            // be empty and dirtyTargetAssemblies will be not empty.
            // Or if only an explicitly referenced precompiled assembly without any
            // references is marked as dirty, then scriptAssemblies will be empty and
            // dirtyPrecompiledAssemblies will not be dirty.
            // Then we should delete unused assemblies and clear compilationTask and
            // return CompileStatus.CompilationComplete to indicate to native that compilation was
            // successful and that the assemblies should be reloaded.
            // If the last script of assembly was removed or precompiled assemblies are dirty
            // along with modifying other scripts, then scriptAssemblies will not be empty and
            // we will recompile scripts and then reload assemblies if compilation is successful.
            bool returnCompilationComplete = scriptAssemblies.Length == 0 &&
                (dirtyState.DirtyTargetAssemblies.Any() || dirtyState.DirtyPrecompiledAssemblies.Any());

            // Mark current assembly dirty state as not dirty and track updates to
            // dirty assemblies as compilation of assemblies completes, see
            // call to dirtyState.RemoveCompiledTargetAssembly. Once any
            // script/assembly is modified, IsDirty will be set to
            // true again.
            dirtyState.IsDirty = false;

            if (returnCompilationComplete)
            {
                DeleteUnusedAssemblies(scriptAssemblySettings, fileIOProvider, directoryIOProvider);
                CleanUpAfterCompilationCompleted();
                return CompileStatus.CompilationComplete;
            }

            if (!scriptAssemblies.Any())
            {
                dirtyState.ClearAll();
                return CompileStatus.Idle;
            }

            bool compiling =
                CompileScriptAssemblies(
                    scriptAssemblies,
                    scriptAssemblySettings,
                    tempBuildDirectory,
                    compilationTaskOptions,
                    CompileScriptAssembliesOptions.none);

            return compiling ? CompileStatus.CompilationStarted : CompileStatus.Idle;
        }

        internal bool CompileCustomScriptAssemblies(EditorScriptCompilationOptions options, BuildTargetGroup platformGroup, BuildTarget platform)
        {
            var scriptAssemblySettings = CreateScriptAssemblySettings(platformGroup, platform, options);
            return CompileCustomScriptAssemblies(scriptAssemblySettings, EditorTempPath, platformGroup, platform);
        }

        internal bool CompileCustomScriptAssemblies(ScriptAssemblySettings scriptAssemblySettings, string tempBuildDirectory, BuildTargetGroup platformGroup, BuildTarget platform)
        {
            DeleteUnusedAssemblies();
            var scriptAssemblies = GetAllScriptAssembliesOfType(scriptAssemblySettings, TargetAssemblyType.Custom);

            return CompileScriptAssemblies(scriptAssemblies, scriptAssemblySettings, tempBuildDirectory, CompilationTaskOptions.None, CompileScriptAssembliesOptions.skipSetupChecks);
        }

        internal bool CompileScriptAssemblies(ScriptAssembly[] scriptAssemblies,
            ScriptAssemblySettings scriptAssemblySettings,
            string tempBuildDirectory,
            CompilationTaskOptions compilationTaskOptions,
            CompileScriptAssembliesOptions compileScriptAssembliesOptions)
        {
            StopAllCompilation();

            bool skipSetupChecks = (compileScriptAssembliesOptions & CompileScriptAssembliesOptions.skipSetupChecks) == CompileScriptAssembliesOptions.skipSetupChecks;

            // Skip setup checks when compiling custom script assemblies on startup,
            // as we only load the ones that been compiled and have all their references
            // fully resolved.
            if (!skipSetupChecks && !skipCustomScriptAssemblyGraphValidation)
            {
                // Do no start compilation if there is an setup error.
                if (CompilationSetupErrorsTracker.HaveCompilationSetupErrors())
                    return false;

                CheckCyclicAssemblyReferences();
            }

            if (!Directory.Exists(scriptAssemblySettings.OutputDirectory))
                Directory.CreateDirectory(scriptAssemblySettings.OutputDirectory);

            if (!Directory.Exists(tempBuildDirectory))
                Directory.CreateDirectory(tempBuildDirectory);

            // CodeGen/ILPostProcessor
            var scriptCodegenAssemblies = UnityCodeGenHelpers.ToScriptCodeGenAssemblies(scriptAssemblies);
            var scriptAssembliesCodegen = scriptCodegenAssemblies.CodeGenAssemblies;

            // Do compile codegen assemblies that were scheduled for compilation
            // because one of it's references changed. Only compile them when
            // their source files are modified or the compilation is forced.
            if (scriptAssembliesCodegen.Any())
            {
                scriptAssembliesCodegen = scriptAssembliesCodegen.Where(a => a.DirtySource != DirtySource.DirtyReference).ToList();
            }

            // Compile to tempBuildDirectory
            compilationTask = new CompilationTask(
                scriptCodegenAssemblies.ScriptAssemblies.ToArray(),
                scriptAssembliesCodegen.ToArray(),
                tempBuildDirectory,
                "Editor Compilation",
                scriptAssemblySettings.CompilationOptions,
                compilationTaskOptions,
                maxConcurrentCompilers,
                ILPostProcessing,
                compilerFactory);

            compilationTask.OnCompilationTaskStarted += (context) =>
            {
                Console.WriteLine("- Starting script compilation");
                var stopwatch = new Stopwatch();
                stopwatch.Start();
                stopWatchDict[context] = stopwatch;

                InvokeCompilationStarted(context);
            };

            compilationTask.OnCompilationTaskFinished += (context) =>
            {
                if (!compilationTask.CompileErrors)
                {
                    dirtyState.ClearAll();
                }

                InvokeCompilationFinished(context);

                var stopwatch = stopWatchDict[context];
                var elapsed = stopwatch.Elapsed;
                stopWatchDict.Remove(context);

                Console.WriteLine($"- Finished script compilation in {elapsed.TotalSeconds:0.######} seconds");
            };

            compilationTask.OnBeforeCompilationStarted += (assembly, phase) =>
            {
                if (runScriptUpdaterAssemblies.Contains(assembly.Filename))
                    runScriptUpdaterAssemblies.Remove(assembly.Filename);

                RunScriptUpdater(assembly, tempBuildDirectory, scriptAssemblySettings.CompilationOptions);
            };

            compilationTask.OnCompilationStarted += (assembly, phase) =>
            {
                var stopwatch = new Stopwatch();
                stopwatch.Start();
                var assemblyOutputPath = AssetPath.Combine(scriptAssemblySettings.OutputDirectory, assembly.Filename);
                Console.WriteLine("- Starting compile {0}", assemblyOutputPath);
                InvokeAssemblyCompilationStarted(assemblyOutputPath);
                stopWatchDict[assemblyOutputPath] = stopwatch;
            };

            compilationTask.OnCompilationFinished += (assembly, messages) =>
            {
                assembly.GeneratedResponseFile = null;

                var assemblyOutputPath = AssetPath.Combine(scriptAssemblySettings.OutputDirectory, assembly.Filename);

                if (!assembly.HasCompileErrors)
                {
                    dirtyState.RemoveCompiledTargetAssembly(assembly.Filename);
                }

                AddChangedAssembly(assembly.Filename, scriptAssemblySettings.CompilationOptions);

                if (assembly.HasCompileErrors)
                {
                    AddUnitySpecificErrorMessages(assembly, messages);
                    InvokeAssemblyCompilationFinished(assembly, messages, scriptAssemblySettings.CompilationOptions);
                    return;
                }

                // Copy from tempBuildDirectory to assembly output directory
                if (!CopyAssembly(AssetPath.Combine(tempBuildDirectory, assembly.Filename), assembly.FullPath, fileIOProvider, out string errorMessage))
                {
                    messages.Add(new CompilerMessage
                    {
                        message = $"Copying assembly from '{AssetPath.Combine(tempBuildDirectory, assembly.Filename)}' to '{assembly.FullPath}' failed. Detailed error: {errorMessage}",
                        type = CompilerMessageType.Error,
                        file = assembly.FullPath,
                        line = -1,
                        column = -1
                    });
                    StopCompilationTask();
                    InvokeAssemblyCompilationFinished(assembly, messages, scriptAssemblySettings.CompilationOptions);
                    return;
                }

                InvokeAssemblyCompilationFinished(assembly, messages, scriptAssemblySettings.CompilationOptions);

                Stopwatch stopwatch = null;

                try
                {
                    stopwatch = stopWatchDict[assemblyOutputPath];
                    var elapsed = stopwatch.Elapsed;
                    Console.WriteLine($"- Finished compile {assemblyOutputPath} in {elapsed.TotalSeconds:0.######} seconds");
                    stopWatchDict.Remove(assemblyOutputPath);
                }
                catch (Exception)
                {
                    Console.WriteLine("- Finished compile {0}", assemblyOutputPath);
                }
            };

            compilationTask.Poll();
            return true;
        }

        static void RunScriptUpdater(
            ScriptAssembly assembly,
            string tempBuildDirectory,
            EditorScriptCompilationOptions options)
        {
            assembly.GeneratedResponseFile = MicrosoftCSharpCompiler.GenerateResponseFile(assembly, tempBuildDirectory);

            APIUpdaterHelper.UpdateScripts(
                assembly.GeneratedResponseFile,
                "cs",
                assembly.Files
            );
        }

        void AddUnitySpecificErrorMessages(ScriptAssembly assembly, List<CompilerMessage> messages)
        {
            var processors = new List<UnitySpecificCompilerMessageProcessor>()
            {
                new UnsafeErrorProcessor(assembly, this),
                new ModuleReferenceErrorProcessor(),
                new DeterministicAssemblyVersionErrorProcessor(),
            };

            if (!messages.Any(m => processors.Any(p => p.IsInterestedInMessage(m))))
                return;

            List<CompilerMessage> newMessages = new List<CompilerMessage>();

            foreach (var message in messages)
            {
                var newMessage = new CompilerMessage(message);
                foreach (var processor in processors)
                {
                    if (processor.IsInterestedInMessage(message))
                        processor.PostprocessMessage(ref newMessage);
                }

                newMessages.Add(newMessage);
            }

            messages.Clear();
            messages.AddRange(newMessages);
        }

        public void InvokeAssemblyCompilationStarted(string assemblyOutputPath)
        {
            if (assemblyCompilationStarted != null)
                assemblyCompilationStarted(assemblyOutputPath);
        }

        public void InvokeAssemblyCompilationFinished(ScriptAssembly assembly, List<CompilerMessage> messages, EditorScriptCompilationOptions scriptCompilationOptions)
        {
            if (assemblyCompilationFinished != null)
            {
                var convertedMessages = ConvertCompilerMessages(messages);
                assemblyCompilationFinished(assembly, convertedMessages, scriptCompilationOptions);
            }
        }

        public void InvokeCompilationStarted(object context)
        {
            if (compilationStarted != null)
                compilationStarted(context);
        }

        public void InvokeCompilationFinished(object context)
        {
            if (compilationFinished != null)
                compilationFinished(context);
        }

        public bool ArePrecompiledAssembliesDirty()
        {
            return dirtyState.IsDirty && (dirtyState.AreAllPrecompiledAssembliesDirty || dirtyState.DirtyPrecompiledAssemblies.Count > 0);
        }

        public bool DoesProjectFolderHaveAnyDirtyScripts()
        {
            return dirtyState.IsDirty &&
                ((dirtyState.AreAllTargetAssembliesDirty && allScripts.Count > 0) ||
                    dirtyState.DirtyTargetAssemblies.Count > 0);
        }

        public bool DoesProjectFolderHaveAnyScripts()
        {
            return allScripts != null && allScripts.Count > 0;
        }

        public bool DoesProjectHaveAnyCustomScriptAssemblies()
        {
            foreach (var entry in allScripts)
            {
                var script = entry.Key;
                var assemblyFilename = entry.Value;

                var targetAssembly = EditorBuildRules.GetTargetAssembly(script, assemblyFilename, projectDirectory, customTargetAssemblies);

                if (targetAssembly.Type == TargetAssemblyType.Custom)
                    return true;
            }

            return false;
        }

        internal ScriptAssemblySettings CreateScriptAssemblySettings(BuildTargetGroup buildTargetGroup, BuildTarget buildTarget, EditorScriptCompilationOptions options)
        {
            return CreateScriptAssemblySettings(buildTargetGroup, buildTarget, options, new string[] {});
        }

        internal ScriptAssemblySettings CreateScriptAssemblySettings(BuildTargetGroup buildTargetGroup, BuildTarget buildTarget, EditorScriptCompilationOptions options, string[] extraScriptingDefines)
        {
            var predefinedAssembliesCompilerOptions = new ScriptCompilerOptions();

            if ((options & EditorScriptCompilationOptions.BuildingPredefinedAssembliesAllowUnsafeCode) == EditorScriptCompilationOptions.BuildingPredefinedAssembliesAllowUnsafeCode)
                predefinedAssembliesCompilerOptions.AllowUnsafeCode = true;

            if ((options & EditorScriptCompilationOptions.BuildingUseDeterministicCompilation) == EditorScriptCompilationOptions.BuildingUseDeterministicCompilation)
                predefinedAssembliesCompilerOptions.UseDeterministicCompilation = true;

            predefinedAssembliesCompilerOptions.ApiCompatibilityLevel = PlayerSettings.GetApiCompatibilityLevel(buildTargetGroup);

            ICompilationExtension compilationExtension = null;
            if ((options & EditorScriptCompilationOptions.BuildingForEditor) == 0)
            {
                compilationExtension = ModuleManager.FindPlatformSupportModule(ModuleManager.GetTargetStringFromBuildTarget(buildTarget))?.CreateCompilationExtension();
            }


            List<string> additionalCompilationArguments = new List<string>(PlayerSettings.GetAdditionalCompilerArgumentsForGroup(buildTargetGroup));

            if (PlayerSettings.suppressCommonWarnings)
            {
                additionalCompilationArguments.Add("/nowarn:0169");
                additionalCompilationArguments.Add("/nowarn:0649");
            }

            var additionalCompilationArgumentsArray = additionalCompilationArguments.Where(s => !string.IsNullOrEmpty(s)).Distinct().ToArray();

            var settings = new ScriptAssemblySettings
            {
                BuildTarget = buildTarget,
                BuildTargetGroup = buildTargetGroup,
                OutputDirectory = GetCompileScriptsOutputDirectory(),
                CompilationOptions = options,
                PredefinedAssembliesCompilerOptions = predefinedAssembliesCompilerOptions,
                CompilationExtension = compilationExtension,
                EditorCodeOptimization = CompilationPipeline.codeOptimization,
                ExtraGeneralDefines = extraScriptingDefines,
                ProjectRootNamespace = EditorSettings.projectGenerationRootNamespace,
                AdditionalCompilerArguments = additionalCompilationArgumentsArray,
            };

            return settings;
        }

        ScriptAssemblySettings CreateEditorScriptAssemblySettings(EditorScriptCompilationOptions options)
        {
            return CreateScriptAssemblySettings(EditorUserBuildSettings.activeBuildTargetGroup, EditorUserBuildSettings.activeBuildTarget, options);
        }

        public AssemblyCompilerMessages[] GetCompileMessages()
        {
            if (compilationTask == null)
                return null;

            var result = new AssemblyCompilerMessages[compilationTask.CompilerMessages.Count];

            int index = 0;
            foreach (var entry in compilationTask.CompilerMessages)
            {
                var assembly = entry.Key;
                var messages = entry.Value;

                result[index++] = new AssemblyCompilerMessages { assemblyFilename = assembly.Filename, messages = messages };
            }

            // Sort compiler messages by assembly filename to make the order deterministic.
            Array.Sort(result, (m1, m2) => String.Compare(m1.assemblyFilename, m2.assemblyFilename));

            return result;
        }

        public bool IsCompilationPending()
        {
            // If there were any errors in setting up the compilation, then return false.
            if (CompilationSetupErrorsTracker.HaveCompilationSetupErrors())
                return false;

            // If we have dirty scripts or script updater has marked assemblies for updated,
            // then compilation will trigger on next TickCompilationPipeline.
            return DoesProjectFolderHaveAnyDirtyScripts() ||
                ArePrecompiledAssembliesDirty() ||
                runScriptUpdaterAssemblies.Count > 0 ||
                recompileAllScriptsOnNextTick;
        }

        public bool IsAnyAssemblyBuilderCompiling()
        {
            if (assemblyBuilders.Count > 0)
            {
                bool isCompiling = false;

                var removeAssemblyBuilders = new List<Compilation.AssemblyBuilder>();

                // Check status of compile tasks
                foreach (var assemblyBuilder in assemblyBuilders)
                {
                    var status = assemblyBuilder.status;

                    if (status == Compilation.AssemblyBuilderStatus.IsCompiling)
                        isCompiling = true;
                    else if (status == Compilation.AssemblyBuilderStatus.Finished)
                        removeAssemblyBuilders.Add(assemblyBuilder);
                }

                // Remove all compile tasks that finished compiling.
                if (removeAssemblyBuilders.Count > 0)
                    assemblyBuilders.RemoveAll(t => removeAssemblyBuilders.Contains(t));

                return isCompiling;
            }

            return false;
        }

        public bool IsCompiling()
        {
            // Native code expects IsCompiling to be true after marking scripts as dirty,
            // therefore return true if the compilation is pending
            return IsCompilationTaskCompiling() || IsCompilationPending() || IsAnyAssemblyBuilderCompiling();
        }

        public bool IsCompilationTaskCompiling()
        {
            return compilationTask != null && compilationTask.IsCompiling;
        }

        public void StopAllCompilation()
        {
            StopCompilationTask();

            if (compilationTask != null)
                compilationTask.Dispose();

            compilationTask = null;
        }

        public void StopCompilationTask()
        {
            if (compilationTask == null)
                return;

            compilationTask.Stop();
        }

        public CompileStatus TickCompilationPipeline(EditorScriptCompilationOptions options, BuildTargetGroup platformGroup, BuildTarget platform, string[] extraScriptingDefines)
        {
            // Return CompileStatus.Compiling if any compile task is still compiling.
            // This ensures that the compile tasks finish compiling before any
            // scripts in the Assets folder are compiled and a domain reload
            // is triggered.
            if (IsAnyAssemblyBuilderCompiling())
                return CompileStatus.Compiling;

            if (recompileAllScriptsOnNextTick)
            {
                DirtyAllScripts();
                recompileAllScriptsOnNextTick = false;
            }

            // If we are not currently compiling and there are new dirty assemblies, start compilation.
            if (!IsCompilationTaskCompiling() && IsCompilationPending())
            {
                Profiler.BeginSample("CompilationPipeline.CompileScripts");
                CompileStatus compileStatus = CompileScripts(options, platformGroup, platform, extraScriptingDefines);
                Profiler.EndSample();
                return compileStatus;
            }

            return PollCompilation();
        }

        public CompileStatus PollCompilation()
        {
            if (IsCompilationTaskCompiling())
            {
                if (compilationTask.Poll()) // Returns true when compilation finished.
                    return compilationTask.CompileErrors ? CompileStatus.CompilationFailed : CompileStatus.CompilationComplete;

                return CompileStatus.Compiling;
            }

            return CompileStatus.Idle;
        }

        public TargetAssemblyInfo[] GetTargetAssemblyInfos()
        {
            TargetAssembly[] predefindTargetAssemblies = EditorBuildRules.GetPredefinedTargetAssemblies();

            TargetAssemblyInfo[] targetAssemblyInfo = new TargetAssemblyInfo[predefindTargetAssemblies.Length + (customTargetAssemblies != null ? customTargetAssemblies.Count : 0)];

            for (int i = 0; i < predefindTargetAssemblies.Length; ++i)
                targetAssemblyInfo[i] = ToTargetAssemblyInfo(predefindTargetAssemblies[i]);

            if (customTargetAssemblies != null)
            {
                int i = predefindTargetAssemblies.Length;
                foreach (var entry in customTargetAssemblies)
                {
                    var customTargetAssembly = entry.Value;
                    targetAssemblyInfo[i] = ToTargetAssemblyInfo(customTargetAssembly);
                    i++;
                }
            }

            return targetAssemblyInfo;
        }

        TargetAssembly[] GetTargetAssemblies()
        {
            TargetAssembly[] predefindTargetAssemblies = EditorBuildRules.GetPredefinedTargetAssemblies();

            TargetAssembly[] targetAssemblies = new TargetAssembly[predefindTargetAssemblies.Length + (customTargetAssemblies != null ? customTargetAssemblies.Count : 0)];

            for (int i = 0; i < predefindTargetAssemblies.Length; ++i)
                targetAssemblies[i] = predefindTargetAssemblies[i];

            if (customTargetAssemblies != null)
            {
                int i = predefindTargetAssemblies.Length;
                foreach (var entry in customTargetAssemblies)
                {
                    var customTargetAssembly = entry.Value;
                    targetAssemblies[i] = customTargetAssembly;
                    i++;
                }
            }

            return targetAssemblies;
        }

        public TargetAssemblyInfo[] GetTargetAssembliesWithScripts(EditorScriptCompilationOptions options)
        {
            ScriptAssemblySettings settings = CreateEditorScriptAssemblySettings(EditorScriptCompilationOptions.BuildingForEditor | options);
            return GetTargetAssembliesWithScripts(settings);
        }

        public TargetAssemblyInfo[] GetTargetAssembliesWithScripts(ScriptAssemblySettings settings)
        {
            UpdateAllTargetAssemblyDefines(customTargetAssemblies, EditorBuildRules.GetPredefinedTargetAssemblies(), m_VersionMetaDatas, settings);

            var targetAssemblies = EditorBuildRules.GetTargetAssembliesWithScripts(allScripts, projectDirectory, customTargetAssemblies, settings);

            var targetAssemblyInfos = new TargetAssemblyInfo[targetAssemblies.Length];

            for (int i = 0; i < targetAssemblies.Length; ++i)
                targetAssemblyInfos[i] = ToTargetAssemblyInfo(targetAssemblies[i]);

            return targetAssemblyInfos;
        }

        public HashSet<TargetAssembly> GetTargetAssembliesWithScriptsHashSet(EditorScriptCompilationOptions options)
        {
            ScriptAssemblySettings settings = CreateEditorScriptAssemblySettings(EditorScriptCompilationOptions.BuildingForEditor | options);
            var targetAssemblies = EditorBuildRules.GetTargetAssembliesWithScriptsHashSet(allScripts, projectDirectory, customTargetAssemblies, settings);

            return targetAssemblies;
        }

        public ScriptAssembly[] GetAllScriptAssembliesForLanguage<T>(EditorScriptCompilationOptions additionalOptions) where T : SupportedLanguage
        {
            var assemblies = GetAllScriptAssemblies(EditorScriptCompilationOptions.BuildingForEditor, null).Where(a => a.Language.GetType() == typeof(T)).ToArray();
            return assemblies;
        }

        public ScriptAssembly GetScriptAssemblyForLanguage<T>(string assemblyNameOrPath, EditorScriptCompilationOptions additionalOptions) where T : SupportedLanguage
        {
            var assemblyName = AssetPath.GetAssemblyNameWithoutExtension(assemblyNameOrPath);
            var scriptAssemblies = GetAllScriptAssembliesForLanguage<T>(additionalOptions);
            return scriptAssemblies.SingleOrDefault(a => String.Compare(assemblyName, AssetPath.GetAssemblyNameWithoutExtension(a.Filename), StringComparison.OrdinalIgnoreCase) == 0);
        }

        public TargetAssembly[] GetCustomTargetAssemblies()
        {
            return customTargetAssemblies.Values.ToArray();
        }

        public CustomScriptAssembly[] GetCustomScriptAssemblies()
        {
            return customScriptAssemblies;
        }

        public PrecompiledAssembly[] GetUnityAssemblies()
        {
            return unityAssemblies;
        }

        public TargetAssemblyInfo GetTargetAssembly(string scriptPath)
        {
            TargetAssembly targetAssembly = EditorBuildRules.GetTargetAssemblyLinearSearch(scriptPath, projectDirectory, customTargetAssemblies);

            TargetAssemblyInfo targetAssemblyInfo = ToTargetAssemblyInfo(targetAssembly);
            return targetAssemblyInfo;
        }

        public TargetAssembly GetTargetAssemblyDetails(string scriptPath)
        {
            return EditorBuildRules.GetTargetAssemblyLinearSearch(scriptPath, projectDirectory, customTargetAssemblies);
        }

        public ScriptAssembly[] GetAllEditorScriptAssemblies(EditorScriptCompilationOptions additionalOptions)
        {
            return GetAllScriptAssemblies(EditorScriptCompilationOptions.BuildingForEditor | EditorScriptCompilationOptions.BuildingIncludingTestAssemblies | additionalOptions, null);
        }

        public ScriptAssembly[] GetAllEditorScriptAssemblies(EditorScriptCompilationOptions additionalOptions, string[] defines)
        {
            return GetAllScriptAssemblies(EditorScriptCompilationOptions.BuildingForEditor | EditorScriptCompilationOptions.BuildingIncludingTestAssemblies | additionalOptions, defines);
        }

        public ScriptAssembly[] GetScriptAssembliesForRoslynAnalysis(string[] candidateAssemblies)
        {
            Dictionary<string, PrecompiledAssembly> precompiledAssemblies =
                GetPrecompiledAssembliesDictionaryWithSetupErrorsTracking(
                    isEditor: true,
                    EditorUserBuildSettings.activeBuildTargetGroup,
                    EditorUserBuildSettings.activeBuildTarget);

            ScriptAssemblySettings settings =
                CreateEditorScriptAssemblySettings(EditorScriptCompilationOptions.BuildingForEditor | EditorScriptCompilationOptions.BuildingWithRoslynAnalysis);

            return GetAllScriptAssemblies(
                settings,
                unityAssemblies,
                precompiledAssemblies,
                defines: null,
                targetAssemblyCondition: assembly => (assembly.Flags & AssemblyFlags.CandidateForCompilingWithRoslynAnalyzers) != 0
                && candidateAssemblies.Contains(assembly.Filename));
        }

        public ScriptAssembly[] GetAllScriptAssemblies(EditorScriptCompilationOptions options, string[] defines)
        {
            var isForEditor = (options & EditorScriptCompilationOptions.BuildingForEditor) == EditorScriptCompilationOptions.BuildingForEditor;
            var precompiledAssemblies = GetPrecompiledAssembliesDictionaryWithSetupErrorsTracking(
                isForEditor, EditorUserBuildSettings.activeBuildTargetGroup, EditorUserBuildSettings.activeBuildTarget);
            return GetAllScriptAssemblies(options, unityAssemblies, precompiledAssemblies, defines);
        }

        public ScriptAssembly[] GetAllScriptAssemblies(
            EditorScriptCompilationOptions options,
            PrecompiledAssembly[] unityAssembliesArg,
            Dictionary<string, PrecompiledAssembly> precompiledAssembliesArg,
            string[] defines)
        {
            var settings = CreateEditorScriptAssemblySettings(options);

            return GetAllScriptAssemblies(
                settings,
                unityAssembliesArg,
                precompiledAssembliesArg,
                defines);
        }

        public ScriptAssembly[] GetAllScriptAssemblies(
            ScriptAssemblySettings settings,
            PrecompiledAssembly[] unityAssembliesArg,
            Dictionary<string, PrecompiledAssembly> precompiledAssembliesArg,
            string[] defines,
            Func<TargetAssembly, bool> targetAssemblyCondition = null)
        {
            if (defines != null)
            {
                settings.ExtraGeneralDefines = defines;
            }

            UpdateAllTargetAssemblyDefines(customTargetAssemblies, EditorBuildRules.GetPredefinedTargetAssemblies(), m_VersionMetaDatas, settings);

            var assemblies = new EditorBuildRules.CompilationAssemblies
            {
                UnityAssemblies = unityAssembliesArg,
                PrecompiledAssemblies = precompiledAssembliesArg,
                CustomTargetAssemblies = customTargetAssemblies,
                RoslynAnalyzerDllPaths = PrecompiledAssemblyProvider.GetRoslynAnalyzerPaths(),
                PredefinedAssembliesCustomTargetReferences = GetPredefinedAssemblyReferences(customTargetAssemblies),
                EditorAssemblyReferences = ModuleUtils.GetAdditionalReferencesForUserScripts(),
            };

            return EditorBuildRules.GetAllScriptAssemblies(
                allScripts,
                projectDirectory,
                settings,
                assemblies,
                runScriptUpdaterAssemblies,
                targetAssemblyCondition: targetAssemblyCondition);
        }

        public string[] GetTargetAssemblyDefines(TargetAssembly targetAssembly, ScriptAssemblySettings settings)
        {
            var semVersionRangesFactory = new VersionRangesFactory<SemVersion>();
            var unityVersionRangesFactory = new VersionRangesFactory<UnityVersion>();
            var versionMetaDatas = GetVersionMetaDatas();
            var editorOnlyCompatibleDefines = InternalEditorUtility.GetCompilationDefines(settings.CompilationOptions, settings.BuildTargetGroup, settings.BuildTarget, ApiCompatibilityLevel.NET_4_6);
            var playerAssembliesDefines = InternalEditorUtility.GetCompilationDefines(settings.CompilationOptions, settings.BuildTargetGroup, settings.BuildTarget, settings.PredefinedAssembliesCompilerOptions.ApiCompatibilityLevel);

            return GetTargetAssemblyDefines(targetAssembly, semVersionRangesFactory, unityVersionRangesFactory, versionMetaDatas, editorOnlyCompatibleDefines, playerAssembliesDefines, settings);
        }

        private static string[] GetTargetAssemblyDefines(TargetAssembly targetAssembly, VersionRangesFactory<SemVersion> semVersionRangesFactory, VersionRangesFactory<UnityVersion> unityVersionRangesFactory,
            Dictionary<string, VersionMetaData> versionMetaDatas, string[] editorOnlyCompatibleDefines, string[] playerAssembliesDefines, ScriptAssemblySettings settings)
        {
            string[] settingsExtraGeneralDefines = settings.ExtraGeneralDefines;
            int populatedVersionDefinesCount = 0;

            string[] compilationDefines;
            if ((targetAssembly.Flags & AssemblyFlags.EditorOnly) == AssemblyFlags.EditorOnly)
            {
                compilationDefines = editorOnlyCompatibleDefines;
            }
            else
            {
                compilationDefines = playerAssembliesDefines;
            }

            string[] defines = new string[compilationDefines.Length + targetAssembly.VersionDefines.Count + settingsExtraGeneralDefines.Length];

            Array.Copy(settingsExtraGeneralDefines, defines, settingsExtraGeneralDefines.Length);
            populatedVersionDefinesCount += settingsExtraGeneralDefines.Length;
            Array.Copy(compilationDefines, 0, defines, populatedVersionDefinesCount, compilationDefines.Length);
            populatedVersionDefinesCount += compilationDefines.Length;

            if (versionMetaDatas == null)
            {
                return defines;
            }

            var targetAssemblyVersionDefines = targetAssembly.VersionDefines;

            for (int i = 0; i < targetAssemblyVersionDefines.Count; i++)
            {
                var targetAssemblyVersionDefine = targetAssemblyVersionDefines[i];
                if (!versionMetaDatas.ContainsKey(targetAssemblyVersionDefine.name))
                {
                    continue;
                }

                if (string.IsNullOrEmpty(targetAssemblyVersionDefine.expression))
                {
                    var define = targetAssemblyVersionDefine.define;
                    if (!string.IsNullOrEmpty(define))
                    {
                        defines[populatedVersionDefinesCount] = define;
                        ++populatedVersionDefinesCount;
                    }
                    continue;
                }

                try
                {
                    var versionMetaData = versionMetaDatas[targetAssemblyVersionDefine.name];
                    var versionString = versionMetaData.Version;
                    bool isValid = false;
                    switch (versionMetaData.Type)
                    {
                        case VersionType.VersionTypeUnity:
                        {
                            var versionDefineExpression = unityVersionRangesFactory.GetExpression(targetAssemblyVersionDefine.expression);
                            var unityVersion = UnityVersionParser.Parse(versionString);
                            isValid = versionDefineExpression.IsValid(unityVersion);
                            break;
                        }

                        case VersionType.VersionTypePackage:
                        {
                            var versionDefineExpression = semVersionRangesFactory.GetExpression(targetAssemblyVersionDefine.expression);
                            var semVersion = SemVersionParser.Parse(versionString);
                            isValid = versionDefineExpression.IsValid(semVersion);
                            break;
                        }

                        default:
                            throw new NotImplementedException($"EditorCompilation does not recognize versionMetaData.Type {versionMetaData.Type}. UNIMPLEMENTED");
                    }

                    if (isValid)
                    {
                        defines[populatedVersionDefinesCount] = targetAssemblyVersionDefine.define;
                        ++populatedVersionDefinesCount;
                    }
                }
                catch (Exception e)
                {
                    var asset = AssetDatabase.LoadAssetAtPath<AssemblyDefinitionAsset>(EditorCompilationInterface.Instance.FindCustomTargetAssemblyFromTargetAssembly(targetAssembly).FilePath);
                    UnityEngine.Debug.LogException(e, asset);
                }
            }

            Array.Resize(ref defines, populatedVersionDefinesCount);
            return defines;
        }

        // TODO: Get rid of calls to this method and ensure that the defines are always setup correctly at all times.
        private static void UpdateAllTargetAssemblyDefines(IDictionary<string, TargetAssembly> customScriptAssemblies, TargetAssembly[] predefinedTargetAssemblies,
            Dictionary<string, VersionMetaData> versionMetaDatas, ScriptAssemblySettings settings)
        {
            var allTargetAssemblies = customScriptAssemblies.Values.ToArray()
                .Concat(predefinedTargetAssemblies ?? new TargetAssembly[0]);

            var semVersionRangesFactory = new VersionRangesFactory<SemVersion>();
            var unityVersionRangesFactory = new VersionRangesFactory<UnityVersion>();

            string[] editorOnlyCompatibleDefines = null;

            editorOnlyCompatibleDefines = InternalEditorUtility.GetCompilationDefines(settings.CompilationOptions, settings.BuildTargetGroup, settings.BuildTarget, ApiCompatibilityLevel.NET_4_6);

            var playerAssembliesDefines = InternalEditorUtility.GetCompilationDefines(settings.CompilationOptions, settings.BuildTargetGroup, settings.BuildTarget, settings.PredefinedAssembliesCompilerOptions.ApiCompatibilityLevel);

            foreach (var targetAssembly in allTargetAssemblies)
            {
                SetTargetAssemblyDefines(targetAssembly, semVersionRangesFactory, unityVersionRangesFactory, versionMetaDatas, editorOnlyCompatibleDefines, playerAssembliesDefines, settings);
            }
        }

        private static void SetTargetAssemblyDefines(TargetAssembly targetAssembly, VersionRangesFactory<SemVersion> semVersionRangesFactory, VersionRangesFactory<UnityVersion> unityVersionRangesFactory,
            Dictionary<string, VersionMetaData> versionMetaDatas, string[] editorOnlyCompatibleDefines, string[] playerAssembliesDefines, ScriptAssemblySettings settings)
        {
            targetAssembly.Defines = GetTargetAssemblyDefines(targetAssembly, semVersionRangesFactory, unityVersionRangesFactory, versionMetaDatas, editorOnlyCompatibleDefines, playerAssembliesDefines, settings);
        }

        ScriptAssembly[] GetAllScriptAssembliesOfType(ScriptAssemblySettings settings, TargetAssemblyType type)
        {
            var precompiledAssemblies = GetPrecompiledAssembliesDictionaryWithSetupErrorsTracking(
                settings.BuildingForEditor, settings.BuildTargetGroup, settings.BuildTarget);
            UpdateAllTargetAssemblyDefines(customTargetAssemblies, EditorBuildRules.GetPredefinedTargetAssemblies(), m_VersionMetaDatas, settings);

            var assemblies = new EditorBuildRules.CompilationAssemblies
            {
                UnityAssemblies = unityAssemblies,
                PrecompiledAssemblies = precompiledAssemblies,
                CustomTargetAssemblies = customTargetAssemblies,
                RoslynAnalyzerDllPaths = PrecompiledAssemblyProvider.GetRoslynAnalyzerPaths(),
                PredefinedAssembliesCustomTargetReferences = GetPredefinedAssemblyReferences(customTargetAssemblies),
                EditorAssemblyReferences = ModuleUtils.GetAdditionalReferencesForUserScripts(),
            };

            return EditorBuildRules.GetAllScriptAssemblies(allScripts, projectDirectory, settings, assemblies, runScriptUpdaterAssemblies, type);
        }

        public MonoIsland[] GetAllMonoIslands(EditorScriptCompilationOptions additionalOptions)
        {
            bool isEditor = (additionalOptions & EditorScriptCompilationOptions.BuildingForEditor) == EditorScriptCompilationOptions.BuildingForEditor;
            var precompiledAssemblies = GetPrecompiledAssembliesDictionaryWithSetupErrorsTracking(
                isEditor, EditorUserBuildSettings.activeBuildTargetGroup, EditorUserBuildSettings.activeBuildTarget);
            return GetAllMonoIslands(unityAssemblies, precompiledAssemblies, EditorScriptCompilationOptions.BuildingForEditor | EditorScriptCompilationOptions.BuildingIncludingTestAssemblies | additionalOptions);
        }

        public MonoIsland[] GetAllMonoIslands(PrecompiledAssembly[] unityAssembliesArg, Dictionary<string, PrecompiledAssembly> precompiledAssembliesArg, EditorScriptCompilationOptions options)
        {
            var scriptAssemblies = GetAllScriptAssemblies(options, unityAssembliesArg, precompiledAssembliesArg, null);
            var monoIslands = new MonoIsland[scriptAssemblies.Length];

            for (int i = 0; i < scriptAssemblies.Length; ++i)
                monoIslands[i] = scriptAssemblies[i].ToMonoIsland(EditorScriptCompilationOptions.BuildingForEditor, EditorTempPath);

            return monoIslands;
        }

        public bool IsRuntimeScriptAssembly(string assemblyNameOrPath)
        {
            var assemblyFilename = AssetPath.GetFileName(assemblyNameOrPath);

            if (!assemblyFilename.EndsWith(".dll"))
                assemblyFilename += ".dll";

            var predefinedAssemblyTargets = EditorBuildRules.GetPredefinedTargetAssemblies();

            if (predefinedAssemblyTargets.Any(a => ((a.Flags & AssemblyFlags.EditorOnly) != AssemblyFlags.EditorOnly) && a.Filename == assemblyFilename))
                return true;

            if (customTargetAssemblies != null && customTargetAssemblies.Any(a => ((a.Value.Flags & AssemblyFlags.EditorOnly) != AssemblyFlags.EditorOnly) && a.Value.Filename == assemblyFilename))
                return true;

            return false;
        }

        TargetAssemblyInfo ToTargetAssemblyInfo(TargetAssembly targetAssembly)
        {
            TargetAssemblyInfo targetAssemblyInfo = new TargetAssemblyInfo();

            if (targetAssembly != null)
            {
                targetAssemblyInfo.Name = targetAssembly.Filename;
                targetAssemblyInfo.Flags = targetAssembly.Flags;
            }
            else
            {
                targetAssemblyInfo.Name = "";
                targetAssemblyInfo.Flags = AssemblyFlags.None;
            }

            return targetAssemblyInfo;
        }

        static EditorScriptCompilationOptions ToEditorScriptCompilationOptions(Compilation.AssemblyBuilderFlags flags)
        {
            EditorScriptCompilationOptions options = EditorScriptCompilationOptions.BuildingEmpty;

            if ((flags & Compilation.AssemblyBuilderFlags.DevelopmentBuild) == Compilation.AssemblyBuilderFlags.DevelopmentBuild)
                options |= EditorScriptCompilationOptions.BuildingDevelopmentBuild;

            if ((flags & Compilation.AssemblyBuilderFlags.EditorAssembly) == Compilation.AssemblyBuilderFlags.EditorAssembly)
                options |= EditorScriptCompilationOptions.BuildingForEditor;

            return options;
        }

        static AssemblyFlags ToAssemblyFlags(Compilation.AssemblyBuilderFlags assemblyBuilderFlags)
        {
            AssemblyFlags assemblyFlags = AssemblyFlags.None;

            if ((assemblyBuilderFlags & Compilation.AssemblyBuilderFlags.EditorAssembly) == Compilation.AssemblyBuilderFlags.EditorAssembly)
                assemblyFlags |= AssemblyFlags.EditorOnly;

            return assemblyFlags;
        }

        static EditorBuildRules.UnityReferencesOptions ToUnityReferencesOptions(ReferencesOptions options)
        {
            var result = EditorBuildRules.UnityReferencesOptions.ExcludeModules;

            if ((options & ReferencesOptions.UseEngineModules) == ReferencesOptions.UseEngineModules)
            {
                result = EditorBuildRules.UnityReferencesOptions.None;
            }

            return result;
        }

        ScriptAssembly InitializeScriptAssemblyWithoutReferencesAndDefines(Compilation.AssemblyBuilder assemblyBuilder)
        {
            var scriptFiles = assemblyBuilder.scriptPaths.Select(p => AssetPath.Combine(projectDirectory, p)).ToArray();
            var assemblyPath = AssetPath.Combine(projectDirectory, assemblyBuilder.assemblyPath);

            var scriptAssembly = new ScriptAssembly();
            scriptAssembly.Flags = ToAssemblyFlags(assemblyBuilder.flags);
            scriptAssembly.BuildTarget = assemblyBuilder.buildTarget;
            scriptAssembly.Language = ScriptCompilers.GetLanguageFromExtension(ScriptCompilers.GetExtensionOfSourceFile(assemblyBuilder.scriptPaths[0]));
            scriptAssembly.Files = scriptFiles;
            scriptAssembly.Filename = AssetPath.GetFileName(assemblyPath);
            scriptAssembly.OutputDirectory = AssetPath.GetDirectoryName(assemblyPath);
            scriptAssembly.CompilerOptions = assemblyBuilder.compilerOptions;
            scriptAssembly.CompilerOptions.ApiCompatibilityLevel = PlayerSettings.GetApiCompatibilityLevel(assemblyBuilder.buildTargetGroup);
            scriptAssembly.ScriptAssemblyReferences = new ScriptAssembly[0];
            scriptAssembly.RootNamespace = string.Empty;

            return scriptAssembly;
        }

        public ScriptAssembly CreateScriptAssembly(Compilation.AssemblyBuilder assemblyBuilder)
        {
            var scriptAssembly = InitializeScriptAssemblyWithoutReferencesAndDefines(assemblyBuilder);

            var options = ToEditorScriptCompilationOptions(assemblyBuilder.flags);
            var referencesOptions = ToUnityReferencesOptions(assemblyBuilder.referencesOptions);

            var references = GetAssemblyBuilderDefaultReferences(scriptAssembly, options, referencesOptions);

            if (assemblyBuilder.additionalReferences != null && assemblyBuilder.additionalReferences.Length > 0)
                references = references.Concat(assemblyBuilder.additionalReferences).ToArray();

            if (assemblyBuilder.excludeReferences != null && assemblyBuilder.excludeReferences.Length > 0)
                references = references.Where(r => !assemblyBuilder.excludeReferences.Contains(r)).ToArray();

            var defines = GetAssemblyBuilderDefaultDefines(assemblyBuilder);

            if (assemblyBuilder.additionalDefines != null)
                defines = defines.Concat(assemblyBuilder.additionalDefines).ToArray();

            scriptAssembly.References = references.ToArray();
            scriptAssembly.Defines = defines.ToArray();

            return scriptAssembly;
        }

        string[] GetAssemblyBuilderDefaultReferences(ScriptAssembly scriptAssembly, EditorScriptCompilationOptions options, EditorBuildRules.UnityReferencesOptions unityReferencesOptions)
        {
            bool buildingForEditor = (scriptAssembly.Flags & AssemblyFlags.EditorOnly) == AssemblyFlags.EditorOnly;

            var monolithicEngineAssemblyPath = InternalEditorUtility.GetMonolithicEngineAssemblyPath();

            var unityReferences = EditorBuildRules.GetUnityReferences(scriptAssembly, unityAssemblies, options, unityReferencesOptions);

            var customReferences = EditorBuildRules.GetCompiledCustomAssembliesReferences(scriptAssembly, customTargetAssemblies, GetCompileScriptsOutputDirectory());

            var precompiledAssemblies = GetPrecompiledAssembliesWithSetupErrorsTracking(
                buildingForEditor, EditorUserBuildSettings.activeBuildTargetGroup, EditorUserBuildSettings.activeBuildTarget);
            var precompiledReferences = EditorBuildRules.GetPrecompiledReferences(scriptAssembly, TargetAssemblyType.Custom, options, EditorCompatibility.CompatibleWithEditor, precompiledAssemblies);
            var additionalReferences = MonoLibraryHelpers.GetSystemLibraryReferences(scriptAssembly.CompilerOptions.ApiCompatibilityLevel, scriptAssembly.Language);
            string[] editorReferences = buildingForEditor ? ModuleUtils.GetAdditionalReferencesForUserScripts() : new string[0];

            var references = new List<string>();

            if (unityReferencesOptions == EditorBuildRules.UnityReferencesOptions.ExcludeModules)
                references.Add(monolithicEngineAssemblyPath);

            references.AddRange(unityReferences.Values); // unity references paths
            references.AddRange(customReferences.Concat(precompiledReferences).Concat(editorReferences).Concat(additionalReferences));

            return references.ToArray();
        }

        public string[] GetAssemblyBuilderDefaultReferences(AssemblyBuilder assemblyBuilder)
        {
            var scriptAssembly = InitializeScriptAssemblyWithoutReferencesAndDefines(assemblyBuilder);
            var options = ToEditorScriptCompilationOptions(assemblyBuilder.flags);
            var referencesOptions = ToUnityReferencesOptions(assemblyBuilder.referencesOptions);
            var references = GetAssemblyBuilderDefaultReferences(scriptAssembly, options, referencesOptions);

            return references;
        }

        public string[] GetAssemblyBuilderDefaultDefines(AssemblyBuilder assemblyBuilder)
        {
            var options = ToEditorScriptCompilationOptions(assemblyBuilder.flags);
            var defines = InternalEditorUtility.GetCompilationDefines(options, assemblyBuilder.buildTargetGroup, assemblyBuilder.buildTarget);
            return defines;
        }

        public void AddAssemblyBuilder(UnityEditor.Compilation.AssemblyBuilder assemblyBuilder)
        {
            assemblyBuilders.Add(assemblyBuilder);
        }

        public static UnityEditor.Compilation.CompilerMessage[] ConvertCompilerMessages(List<CompilerMessage> messages)
        {
            var newMessages = new UnityEditor.Compilation.CompilerMessage[messages.Count];

            int index = 0;
            foreach (var message in messages)
            {
                var newMessage = new UnityEditor.Compilation.CompilerMessage();

                newMessage.message = message.message;
                newMessage.file = message.file;
                newMessage.line = message.line;
                newMessage.column = message.column;

                switch (message.type)
                {
                    case CompilerMessageType.Error:
                        newMessage.type = UnityEditor.Compilation.CompilerMessageType.Error;
                        break;

                    case CompilerMessageType.Warning:
                        newMessage.type = UnityEditor.Compilation.CompilerMessageType.Warning;
                        break;
                }

                newMessages[index++] = newMessage;
            }

            return newMessages;
        }
    }
}
