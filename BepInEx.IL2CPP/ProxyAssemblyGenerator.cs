using System;
using System.IO;
using System.Security.Cryptography;
using AssemblyUnhollower;
using BepInEx.Logging;
using Il2CppDumper;
using Reactor.OxygenFilter;
using LogLevel = BepInEx.Logging.LogLevel;

namespace BepInEx.IL2CPP
{
	internal static class ProxyAssemblyGenerator
	{
		public static string GameAssemblyPath => Path.Combine(Paths.GameRootPath, "GameAssembly.dll");

		private static string HashPath => Path.Combine(Preloader.IL2CPPUnhollowedPath, "assembly-hash.txt");

		private static string MappingsHashPath => Path.Combine(Preloader.IL2CPPUnhollowedPath, "mappings-hash.txt");

		private static string MappingsPath => Path.Combine(Paths.BepInExRootPath, "mappings.json");

		private static string TempDumperDirectory => Path.Combine(Preloader.IL2CPPUnhollowedPath, "temp");

		private static ManualLogSource Il2cppDumperLogger = Logger.CreateLogSource("Il2CppDumper");

		private static string ComputeHash(string path)
		{
			using var md5 = MD5.Create();
			using var stream = File.OpenRead(path);

			var hash = md5.ComputeHash(stream);

			return Utility.ByteArrayToString(hash);
		}

		public static bool CheckIfGenerationRequired()
		{
			if (!Directory.Exists(Preloader.IL2CPPUnhollowedPath))
				return true;

			if (!File.Exists(HashPath))
				return true;

			if (ComputeHash(GameAssemblyPath) != File.ReadAllText(HashPath))
			{
				Preloader.Log.LogInfo("Detected a game update, will regenerate proxy assemblies");
				return true;
			}

			if (!File.Exists(MappingsHashPath))
				return true;

			if (ComputeHash(MappingsPath) != File.ReadAllText(MappingsHashPath))
			{
				Preloader.Log.LogInfo("Detected a game update, will regenerate proxy assemblies");
				return true;
			}

			return false;
		}

		public static void GenerateAssemblies()
		{
			var domain = AppDomain.CreateDomain("GeneratorDomain", null, new AppDomainSetup
			{
				ApplicationBase = Paths.BepInExAssemblyDirectory
			});

			var runner = (AppDomainRunner)domain.CreateInstanceAndUnwrap(typeof(AppDomainRunner).Assembly.FullName, typeof(AppDomainRunner).FullName);
			
			runner.Setup(Paths.ExecutablePath, Preloader.IL2CPPUnhollowedPath);
			runner.GenerateAssembliesInternal(new AppDomainListener());

			AppDomain.Unload(domain);

			Directory.Delete(TempDumperDirectory, true);

			File.WriteAllText(HashPath, ComputeHash(GameAssemblyPath));
			File.WriteAllText(MappingsHashPath, ComputeHash(MappingsPath));

			Environment.Exit(0); // TODO "workaround" for crash on first startup
		}

		[Serializable]
		private class AppDomainListener : MarshalByRefObject
		{
			public void DoPreloaderLog(object data, LogLevel level)
			{
				Preloader.Log.Log(level, data);
			}

			public void DoDumperLog(object data, LogLevel level)
			{
				Il2cppDumperLogger.Log(level, data);
			}

			public void DoUnhollowerLog(object data, LogLevel level)
			{
				Preloader.UnhollowerLog.Log(level, data);
			}
		}

		[Serializable]
		private class AppDomainRunner : MarshalByRefObject
		{
			public void Setup(string executablePath, string unhollowedPath)
			{
				Paths.SetExecutablePath(executablePath);
				Preloader.IL2CPPUnhollowedPath = unhollowedPath;
			}

			public void GenerateAssembliesInternal(AppDomainListener listener)
			{
				listener.DoPreloaderLog("Generating Il2CppUnhollower assemblies", LogLevel.Message);

				Directory.CreateDirectory(Preloader.IL2CPPUnhollowedPath);

				foreach (var dllFile in Directory.EnumerateFiles(Preloader.IL2CPPUnhollowedPath, "*.dll", SearchOption.TopDirectoryOnly))
					File.Delete(dllFile);

				string tempDumperDirectory = Path.Combine(Preloader.IL2CPPUnhollowedPath, "temp");
				Directory.CreateDirectory(tempDumperDirectory);


				var dumperConfig = new Config
				{
					GenerateScript = false,
					GenerateDummyDll = true
				};

				listener.DoPreloaderLog("Generating Il2CppDumper intermediate assemblies", LogLevel.Info);

				Il2CppDumper.Il2CppDumper.PerformDump(GameAssemblyPath,
					Path.Combine(Paths.GameRootPath, $"{Paths.ProcessName}_Data", "il2cpp_data", "Metadata", "global-metadata.dat"),
					tempDumperDirectory,
					dumperConfig,
					s => listener.DoDumperLog(s, LogLevel.Debug));


				listener.DoPreloaderLog("Executing Reactor.OxygenFilter", LogLevel.Info);

				var oxygenFilter = new OxygenFilter();

				var dumpedDll = new FileInfo(Path.Combine(tempDumperDirectory, "DummyDll", "Assembly-CSharp.dll"));
				oxygenFilter.Start(new FileInfo(MappingsPath), dumpedDll, dumpedDll);

				listener.DoPreloaderLog("Executing Il2CppUnhollower generator", LogLevel.Info);

				UnhollowerBaseLib.LogSupport.InfoHandler += s => listener.DoUnhollowerLog(s, LogLevel.Info);
				UnhollowerBaseLib.LogSupport.WarningHandler += s => listener.DoUnhollowerLog(s, LogLevel.Warning);
				UnhollowerBaseLib.LogSupport.TraceHandler += s => listener.DoUnhollowerLog(s, LogLevel.Debug);
				UnhollowerBaseLib.LogSupport.ErrorHandler += s => listener.DoUnhollowerLog(s, LogLevel.Error);


				string unityBaseLibDir = Path.Combine(Preloader.IL2CPPUnhollowedPath, "base");

				if (Directory.Exists(unityBaseLibDir))
				{
					listener.DoPreloaderLog("Found base unity libraries", LogLevel.Debug);
				}
				else
				{
					unityBaseLibDir = null;
				}


				var unhollowerOptions = new UnhollowerOptions
				{
					GameAssemblyPath = GameAssemblyPath,
					MscorlibPath = Path.Combine(Paths.GameRootPath, "mono", "Managed", "mscorlib.dll"),
					SourceDir = Path.Combine(tempDumperDirectory, "DummyDll"),
					OutputDir = Preloader.IL2CPPUnhollowedPath,
					UnityBaseLibsDir = unityBaseLibDir,
					NoCopyUnhollowerLibs = true
				};

				AssemblyUnhollower.Program.Main(unhollowerOptions);
			}
		}
	}
}