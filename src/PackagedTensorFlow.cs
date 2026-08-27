namespace LostTech.TensorFlow {
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Runtime.InteropServices;
    using System.Text.RegularExpressions;
    using LostTech.WhichPython;
    using Microsoft.Extensions.DependencyModel;
    using static System.FormattableString;
    using static System.Runtime.InteropServices.OSPlatform;
    using static System.Runtime.InteropServices.RuntimeInformation;

    public static class PackagedTensorFlow {
        static readonly string AssemblyPath = Assembly.GetExecutingAssembly().Location;
        static readonly string AssemblyDirectory = Path.GetDirectoryName(AssemblyPath);
        public static PythonEnvironment EnsureDeployed(DirectoryInfo target) {
            if (target is null) throw new ArgumentNullException(nameof(target));
            if (PathEx.IsNested(root: target.FullName, item: AssemblyPath))
                throw new ArgumentException("Can not deploy over this assembly");

            if (ProcessArchitecture != Architecture.X64)
                throw new PlatformNotSupportedException(ProcessArchitecture.ToString());

            string platform = IsOSPlatform(Windows) ? "win"
                : IsOSPlatform(Linux) ? "linux"
                : IsOSPlatform(OSX) ? "osx"
                : throw new PlatformNotSupportedException();

            var runtimeLibrary = DependencyContext.Default.RuntimeLibraries
                .Single(lib => lib.Type == "package" && lib.Name == $"LostTech.TensorFlow.Python.runtime.{platform}-x64");
            string? packagePath = PackageHelper.TryLocatePackage(runtimeLibrary.Path);
            if (packagePath is null)
                throw new PlatformNotSupportedException($"Unable to find runtime package in {nameof(DependencyContext)}");

            string archivePath = runtimeLibrary.NativeLibraryGroups
                .SelectMany(group => group.AssetPaths)
                .Single(path => path.EndsWith("/TensorFlow.tar.xz", StringComparison.Ordinal))
                .Replace('/', Path.DirectorySeparatorChar);

            archivePath = Path.Combine(packagePath, archivePath);
            if (!File.Exists(archivePath)) {
                throw new FileNotFoundException(
                    message: "Packaged TensorFlow was not found. Perhaps the platform is not supported.",
                    fileName: archivePath);
            }

            string relativeInterpreterPath = IsOSPlatform(Windows) ? "python.exe"
                : IsOSPlatform(Linux) || IsOSPlatform(OSX) ? Path.Combine("bin", "python")
                : throw new PlatformNotSupportedException();

            string interpreterPath = Path.Combine(target.FullName, relativeInterpreterPath);
            // TODO: more robust detection
            if (!File.Exists(interpreterPath)) Archive.Extract(archivePath, target.FullName);

            // Python's native dependencies (zlib.dll, vcruntime140.dll etc.)
            // are deployed next to pythonXY.dll, where the OS module loader
            // does not look by itself. Add those directories to the process'
            // search path, like Conda environment activation does.
            if (IsOSPlatform(Windows)) {
                AddToProcessPath(target.FullName);
                AddToProcessPath(Path.Combine(target.FullName, "Library", "bin"));
            }

            var version = DetectPythonVersion(target) ?? DefaultPythonVersion;

            string dllName = IsOSPlatform(Windows)
                ? Invariant($"python{version.Major}{version.Minor}.dll")
                : Path.Combine("lib", UnixPythonLibraryName(target, version));

            target.Refresh();

            return new PythonEnvironment(
                // TODO: support non-Windows
                interpreterPath: new FileInfo(interpreterPath),
                home: target,
                // TODO: support non-Windows
                dll: new FileInfo(Path.Combine(target.FullName, dllName)),
                languageVersion: version,
                Architecture.X64);
        }

        static void AddToProcessPath(string directory) {
            const string name = "PATH";
            string path = Environment.GetEnvironmentVariable(name) ?? string.Empty;
            if (path.Split(Path.PathSeparator).Contains(directory, StringComparer.OrdinalIgnoreCase))
                return;
            Environment.SetEnvironmentVariable(name, directory + Path.PathSeparator + path);
        }

        /// <summary>
        /// The version of Python, that is packaged together with TensorFlow.
        /// Used as a fallback, when automatic detection is not possible.
        /// </summary>
        static readonly Version DefaultPythonVersion = new Version(3, 10);

        /// <summary>
        /// The version of the packaged Python interpreter is not recorded
        /// anywhere explicitly, so it is detected from the names of the
        /// Python shared libraries, deployed alongside it
        /// (e. g. <c>python310.dll</c> or <c>libpython3.10.so</c>).
        /// </summary>
        static Version? DetectPythonVersion(DirectoryInfo environment) {
            var candidates = IsOSPlatform(Windows)
                ? WindowsCandidates(environment)
                : UnixCandidates(environment);
            return candidates.OrderByDescending(version => version).FirstOrDefault();

            static IEnumerable<Version> WindowsCandidates(DirectoryInfo environment) {
                if (!environment.Exists)
                    return Enumerable.Empty<Version>();
                var pattern = new Regex(@"^python(\d)(\d+)\.dll$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                return environment.EnumerateFiles()
                    .Select(file => pattern.Match(file.Name))
                    .Where(match => match.Success)
                    .Select(match => new Version(
                        int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                        int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture)));
            }

            static IEnumerable<Version> UnixCandidates(DirectoryInfo environment) {
                var libDirectory = new DirectoryInfo(Path.Combine(environment.FullName, "lib"));
                if (!libDirectory.Exists)
                    return Enumerable.Empty<Version>();
                var pattern = new Regex(@"^libpython(\d+)\.(\d+)[a-z]*\.",
                    RegexOptions.CultureInvariant);
                return libDirectory.EnumerateFileSystemInfos()
                    .Select(entry => pattern.Match(entry.Name))
                    .Where(match => match.Success)
                    .Select(match => new Version(
                        int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                        int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture)));
            }
        }

        /// <summary>
        /// Returns the name of the Python shared library file, located in the
        /// <c>lib</c> subdirectory of the given environment.
        /// E. g. <c>libpython3.10.so</c> or <c>libpython3.10.dylib</c>.
        /// Prefers the shortest matching name, so that versioned files, like
        /// <c>libpython3.10.so.1.0</c>, are only used when no better option exists.
        /// </summary>
        static string UnixPythonLibraryName(DirectoryInfo environment, Version version) {
            // The "m" ABI suffix was only used before Python 3.8 (see PEP 3149)
            string prefix = version.Minor >= 8
                ? Invariant($"libpython{version.Major}.{version.Minor}")
                : Invariant($"libpython{version.Major}.{version.Minor}m");

            var libDirectory = new DirectoryInfo(Path.Combine(environment.FullName, "lib"));
            if (libDirectory.Exists) {
                string? match = libDirectory.EnumerateFileSystemInfos()
                    .Select(entry => entry.Name)
                    .Where(name => name.StartsWith(prefix, StringComparison.Ordinal))
                    .OrderBy(name => name.Length)
                    .FirstOrDefault(name => name.EndsWith(".so", StringComparison.Ordinal)
                                          || name.EndsWith(".dylib", StringComparison.Ordinal)
                                          || name.IndexOf(".so.", StringComparison.Ordinal) >= 0);
                if (match != null)
                    return match;
            }

            return prefix + (IsOSPlatform(OSX) ? ".dylib" : ".so");
        }
    }
}
