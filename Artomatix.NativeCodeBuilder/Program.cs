using CommandLine;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using System.Xml.Serialization;

namespace Artomatix.NativeCodeBuilder
{
    public class Program
    {

        enum ActionRequired
        {
            None,
            Build,
            Regenerate
        }
        public class CustomXMLWriter : XmlTextWriter
        {
            public CustomXMLWriter(Stream stream) : base(stream, Encoding.UTF8)
            {
                this.Formatting = Formatting.Indented;
            }

            public override void WriteEndElement()
            {
                base.WriteFullEndElement();
            }
        }

        public enum Error
        {
            Success = 0,
            InvalidArgs = 1,
            NativeSettingsNotFound = 2,
            NativeCodePathNotFound = 3,
            CMakeConfigureStepError = 4,
            CMakeBuildError = 5,
            BuildStampPathNotSet = 6
        }

        [Verb(nameof(BuildArgs), true)]
        private class BuildArgs
        {
            [Value(0, Required = true, MetaName = nameof(ProjectDir))]
            public string ProjectDir { get; set; }

            [Value(1, Required = true, MetaName = nameof(Configuration))]
            public string Configuration { get; set; }
        }

        [Verb("create", false)]
        public class CreateArgs
        {
            [Option('p', "path")]
            public string PathToNativeCode { get; set; }

            [Option('t', "targets")]
            public IEnumerable<string> Targets { get; set; }

            [Option('c', "cmakeArgs")]
            public string CMakeArgs { get; set; }

            [Option('o', "outputPath", Required = true)]
            public string OutputPath { get; set; }

            [Option('b', "buildFolderBase")]
            public string BuildFolderBase { get; set; }

            [Option('y', "yes")]
            public bool Yes { get; set; }
        }

        public static int Main(string[] args)
        {
            var buildOpts = default(BuildArgs);
            var createOpts = default(CreateArgs);

            Parser.Default.ParseArguments(args,
                typeof(BuildArgs),
                typeof(CreateArgs))
                .WithParsed<BuildArgs>(parsed =>
                {
                    buildOpts = parsed;
                }).WithParsed<CreateArgs>(parsed =>
                {
                    createOpts = parsed;
                });
            if (buildOpts != null)
            {
                return HandleBuildCommand(buildOpts);
            }
            else if (createOpts != null)
            {
                return HandleCreateCommand(createOpts);
            }

            return 1;
        }

        private static int HandleCreateCommand(CreateArgs args)
        {
            var settings = new NativeCodeSettings()
            {
                CMakeGenerationArguments = args.CMakeArgs ?? "",
                DLLTargets = args.Targets.ToArray(),
                PathToNativeCodeBase = args.PathToNativeCode ?? "",
                BuildPathBase = args.BuildFolderBase ?? ""
            };

            bool consentGiven = !File.Exists(args.OutputPath) || args.Yes;

            if (!consentGiven)
            {
                Console.WriteLine($"File exists at {args.OutputPath}");
                Console.Write("Overwrite? [yN]: ");
            }

            while (!consentGiven)
            {
                var key = (char)Console.Read();

                if (key == 'n' || key == 'N' || key == '\n' || key == '\r')
                {
                    Console.WriteLine("Cancelling...");
                    return 0;
                }
                else if (key == 'y' || key == 'Y')
                {
                    consentGiven = true;
                }
                else
                {
                    Console.Write("Please enter yes or no [yN]:");
                }
            }
            var serializer = new XmlSerializer(typeof(NativeCodeSettings));

            using (var fileStream = File.Open(args.OutputPath, FileMode.Create))
            {
                var writer = new CustomXMLWriter(fileStream);

                serializer.Serialize(writer, settings);
            }

            return 0;
        }

        static ActionRequired CheckActionNeededToBuild(string stampPath, string nativeSettingsPath, string[] extensions, string baseDir)
        {
            if (!File.Exists(stampPath))
            {
                Console.WriteLine("No build stamp detected, regenerating");
                return ActionRequired.Regenerate;
            }

            if (extensions == null)
            {
                return ActionRequired.Regenerate;
            }

            var settingsFileInfo = new FileInfo(nativeSettingsPath);
            var stampLastWritten = new FileInfo(stampPath).LastWriteTimeUtc;

            if (settingsFileInfo.LastWriteTimeUtc > stampLastWritten)
            {
                Console.WriteLine($"Change detected to NativCodeeSettings at {nativeSettingsPath}, Regenerating.");
                return ActionRequired.Regenerate;
            }

            extensions = extensions
                .Select(f => f.ToLowerInvariant())
                .Select(f => f.Trim()).ToArray();

            var options = new EnumerationOptions
            {
                BufferSize = (int)Math.Pow(2, 16),
                MaxRecursionDepth = (int)Math.Pow(2, 8),
                RecurseSubdirectories = true
            };

            var allFiles = Directory
                .EnumerateFiles(baseDir, "*.*", options);

            var sourceFileWrittenTimes = allFiles
                .Where(s => extensions.Contains(Path.GetExtension(s).TrimStart('.').ToLowerInvariant()))
                .Select(s => new { Path = s, Info = new FileInfo(s) })
                .Where(s => s.Info.LastWriteTimeUtc > stampLastWritten);

            var anySourceNewerThanStamp = sourceFileWrittenTimes.Any();

            if (anySourceNewerThanStamp)
            {
                foreach (var item in sourceFileWrittenTimes)
                {
                    Console.WriteLine($"Change detected in source file {item.Path}");
                }
            }

            var cmakeListFileWrittenTimes = allFiles
                .Where(s => Path.GetFileName(s).ToLowerInvariant() == "cmakelists.txt")
                .Select(s => new { Path = s, Info = new FileInfo(s) })
                .Where(s => s.Info.LastWriteTimeUtc > stampLastWritten);

            var anyCMakeListsNewerThanStamp = cmakeListFileWrittenTimes.Any();
            if (anyCMakeListsNewerThanStamp)
            {
                foreach (var item in cmakeListFileWrittenTimes)
                {
                    Console.WriteLine($"Change detected in CMakeLists.txt file {item.Path}");
                }
            }

            if (anyCMakeListsNewerThanStamp)
            {
                return ActionRequired.Regenerate;
            }
            else if (anySourceNewerThanStamp && !anyCMakeListsNewerThanStamp)
            {
                return ActionRequired.Build;
            }
            else
            {
                return ActionRequired.None;
            }
        }

        private static int HandleBuildCommand(BuildArgs args)
        {
            var projectDir = args.ProjectDir;

            if (!Path.IsPathRooted(projectDir))
            {
                projectDir = Path.GetFullPath(projectDir);
            }

            var configuration = args.Configuration;

            string buildTools = "v141";
            bool vs2019 = false;

            var platform = Helpers.GetCurrentPlatform();

            var originalArch = Helpers.GetArchString();

            var arch = Environment.Is64BitProcess && platform == Platform.Windows && !vs2019
                ? "Win64"
                : string.Empty;

            if (vs2019)
            {
                arch = originalArch;
            }

            var settingsPath = Path.Join(projectDir, "NativeCodeSettings.xml");

            if (!File.Exists(settingsPath))
            {
                Console.Error.WriteLine($"Native settings file not found: {settingsPath}");

                return (int)Error.NativeSettingsNotFound;
            }

            var serializer = new XmlSerializer(typeof(NativeCodeSettings));

            INativeCodeSettings settings = null;
            using (var file = File.OpenRead(settingsPath))
            {
                settings = (NativeCodeSettings)serializer.Deserialize(file);
            }

            var stampPath = $"{settings.BuildStampPath}_{configuration}";

            if (String.IsNullOrWhiteSpace(stampPath))
            {
                Console.WriteLine("No build stamp path set");
                return (int)Error.BuildStampPathNotSet;
            }
            else
            {
                if (!Path.IsPathRooted(stampPath))
                {
                    stampPath = Path.GetFullPath(stampPath, projectDir);
                }
            }

            var nativeCodePath = Path.GetFullPath(Path.Join(projectDir, settings.PathToNativeCodeBase));

            if (!Directory.Exists(nativeCodePath))
            {
                Console.Error.Write(
                    $"Your native source code directory ({nativeCodePath}) doesn't exist!\n" +
                    $"Edit this file to change it: {settingsPath}\n" +
                    $"Current contents:{string.Join(Environment.NewLine, settings)}");

                return (int)Error.NativeCodePathNotFound;
            }

            var actionRequested = CheckActionNeededToBuild(stampPath, settingsPath, settings.NativeFileExtensions, nativeCodePath);

            if (actionRequested == ActionRequired.None)
            {
                Console.WriteLine("No changes detected since last build -- quitting");
                return (int)Error.Success;
            }

            var buildDir = Path.Combine(nativeCodePath, $"{settings.BuildPathBase}_{configuration}_{originalArch}");

            Console.WriteLine("buildDir is " + buildDir);
            Console.WriteLine($"CMake Generations Args are {settings.CMakeGenerationArguments}");
            Console.WriteLine($"CMake Build Args are {settings.CMakeBuildArguments}");

            if (!Directory.Exists(buildDir))
            {
                Directory.CreateDirectory(buildDir);
            }

            string generator = null;
            var generatorArgument = !String.IsNullOrEmpty(settings.CMakeGenerator) ? $"-G \"{generator} {arch}\" " : "";

            string cfargs;

            if (vs2019)
            {
                cfargs = $@"-S {nativeCodePath} -G ""{generator}\"" -A {arch} -T {buildTools} -B {buildDir} {settings.CMakeGenerationArguments} -DCMAKE_INSTALL_PREFIX={buildDir}/inst";
            }
            else
            {
                cfargs = $@"-S {nativeCodePath} {generatorArgument} -B {buildDir} {settings.CMakeGenerationArguments} -DCMAKE_INSTALL_PREFIX={buildDir}/inst";
            }


            if (actionRequested == ActionRequired.Regenerate)
            {
                Console.WriteLine($"CMake Generation args are {cfargs}");
                var cmakeConfigureLaunchArgs = new ProcessStartInfo("cmake", cfargs)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };

                var cmakeConfigureProcess = new Process
                {
                    StartInfo = cmakeConfigureLaunchArgs
                };

                cmakeConfigureProcess.OutputDataReceived += WriteOutput;
                cmakeConfigureProcess.ErrorDataReceived += WriteOutput;

                cmakeConfigureProcess.Start();

                cmakeConfigureProcess.BeginOutputReadLine();
                cmakeConfigureProcess.BeginErrorReadLine();

                cmakeConfigureProcess.WaitForExit();

                if (cmakeConfigureProcess.ExitCode != 0)
                {
                    Console.Error.WriteLine($"CMake exited with non-zero error code: {cmakeConfigureProcess.ExitCode}.");
                    Console.Error.WriteLine($"Deleting the {buildDir} directory might fix this.");

                    return (int)Error.CMakeConfigureStepError;
                }
            }

            var buildArgs = $"--build {buildDir} --target install --config {configuration} {settings.CMakeBuildArguments}";

            Console.WriteLine($"Calling cmake --build with {buildArgs}");
            var cmakeBuildLaunchArgs = new ProcessStartInfo("cmake", buildArgs)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };

            var cmakeBuildProcess = new Process
            {
                StartInfo = cmakeBuildLaunchArgs
            };

            cmakeBuildProcess.OutputDataReceived += WriteOutput;
            cmakeBuildProcess.ErrorDataReceived += WriteOutput;

            cmakeBuildProcess.Start();

            cmakeBuildProcess.BeginOutputReadLine();
            cmakeBuildProcess.BeginErrorReadLine();

            cmakeBuildProcess.WaitForExit();

            if (cmakeBuildProcess.ExitCode != 0)
            {
                Console.Error.WriteLine(
                    "If you see an error above about something not existing " +
                    "and mentioning the install target then you will need to add installing to your cmake script." +
                    "The simplest way to do this is:" +
                    "install (TARGETS YOURTARGET ARCHIVE DESTINATION lib LIBRARY DESTINATION lib RUNTIME DESTINATION lib)");

                return (int)Error.CMakeBuildError;
            }

            var dllExtension = platform == Platform.Windows ? "dll" : "so";

            var embeddedFilesPath = Path.Combine(projectDir, "embedded_files");

            if (!Directory.Exists(embeddedFilesPath))
            {
                Directory.CreateDirectory(embeddedFilesPath);
            }

            for (var index = 0; index < settings.DLLTargets.Length; index++)
            {
                var prefix = platform != Platform.Windows ? "lib" : string.Empty;
                var debugPostfix = "d";
                var filenamesToTry = new string[]{
                    $"{prefix}{settings.DLLTargets[index]}.{dllExtension}",
                    $"{prefix}{settings.DLLTargets[index]}{debugPostfix}.{dllExtension}"
                };

                var dllParentPath = Path.Join(buildDir, "inst", "lib");

                foreach (var name in filenamesToTry)
                {
                    var dllToCopy = Path.Join(dllParentPath, name);
                    var filename = Path.GetFileNameWithoutExtension(dllToCopy);
                    var dest = Path.Join(embeddedFilesPath, $"{filename}.{dllExtension}");
                    try
                    {
                        File.Copy(dllToCopy, dest, overwrite: true);
                        Console.WriteLine($"Sucessfully copied {dllToCopy} to {dest}");
                        var pdbFileName = Path.ChangeExtension(name, "pdb");
                        var pdbPath = Path.Join(dllParentPath, pdbFileName);
                        var pdbDestPath = Path.Join(embeddedFilesPath, pdbFileName);

                        if (File.Exists(pdbPath))
                        {
                            File.Copy(pdbPath, pdbDestPath);
                        }

                        break;
                    }
                    catch
                    {
                        Console.WriteLine($"Failed to copy {dllToCopy} to {dest}");
                    }
                }

            }

            if (!String.IsNullOrWhiteSpace(stampPath))
            {
                if (File.Exists(stampPath))
                {
                    File.SetLastWriteTimeUtc(stampPath, DateTime.UtcNow);
                }
                else
                {
                    using (File.Create(stampPath))
                    {

                    }
                }
            }

            Console.WriteLine("Work complete");

            return (int)Error.Success;
        }

        private static void WriteOutput(object sender, DataReceivedEventArgs evt)
        {
            Console.WriteLine($"CMake: {evt.Data}");
        }
    }
}
