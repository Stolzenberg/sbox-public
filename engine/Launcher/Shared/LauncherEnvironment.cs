using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Sandbox;

public static class LauncherEnvironment
{
	/// <summary>
	/// The folder containing sbox.exe
	/// </summary>
	public static string GamePath { get; set; }

	/// <summary>
	/// The folder containing Sandbox.Engine.dll
	/// </summary>
	public static string ManagedDllPath { get; set; }

	public static string PlatformName
	{
		get
		{
			var platform = OperatingSystem.IsWindows() ? "win"
				: OperatingSystem.IsLinux() ? "linuxsteamrt"
				: OperatingSystem.IsMacOS() ? "osx"
				: throw new Exception( "Unsupported platform" );

			var architecture = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "64";
			return $"{platform}{architecture}";
		}
	}

	public static void Init()
	{
		AppDomain.CurrentDomain.AssemblyResolve += CurrentDomain_AssemblyResolve;

		GamePath = System.IO.Path.TrimEndingDirectorySeparator( AppContext.BaseDirectory );

		// this exe is in a folder inside bin - bin/win64, bin/managed
		if ( System.IO.Path.GetFileName( System.IO.Path.GetDirectoryName( GamePath ) ) == "bin" )
		{
			// go up two folders
			GamePath = System.IO.Path.GetDirectoryName( GamePath );
			GamePath = System.IO.Path.GetDirectoryName( GamePath );
		}

		// this exe is in the game folder
		ManagedDllPath = $"{GamePath}/bin/managed/";
		var nativeDllPath = $"{GamePath}/bin/{PlatformName}/";

		// make the game dir our current dir
		Environment.CurrentDirectory = GamePath;

		//
		// Allows unit tests and csproj to find the engine path.
		//
		if ( System.Environment.GetEnvironmentVariable( "FACEPUNCH_ENGINE", EnvironmentVariableTarget.User ) != GamePath )
		{
			System.Environment.SetEnvironmentVariable( "FACEPUNCH_ENGINE", GamePath, EnvironmentVariableTarget.User );
		}

		EnsureNativeLibraryPrecedence( nativeDllPath );

		UpdateNativeDllPath( nativeDllPath );
	}

	private const string PreloadGuardVariable = "SBOX_LINUX_PRELOAD";

	private static void EnsureNativeLibraryPrecedence( string nativeDllPath )
	{
		if ( !OperatingSystem.IsLinux() ) return;

		// Set by the child below - without this the re-exec would recurse forever.
		if ( Environment.GetEnvironmentVariable( PreloadGuardVariable ) == "1" ) return;

		var bundled = Path.Combine( nativeDllPath, "libHarfBuzzSharp.so" );
		if ( !File.Exists( bundled ) ) return;

		var existing = Environment.GetEnvironmentVariable( "LD_PRELOAD" );

		// A wrapper script or Steam launch option already did this.
		if ( existing is not null && existing.Contains( bundled, StringComparison.Ordinal ) ) return;

		var executable = Environment.ProcessPath;
		if ( string.IsNullOrEmpty( executable ) ) return;

		var info = new ProcessStartInfo( executable )
		{
			UseShellExecute = false,
			WorkingDirectory = Environment.CurrentDirectory
		};

		foreach ( var argument in Environment.GetCommandLineArgs().Skip( 1 ) )
		{
			info.ArgumentList.Add( argument );
		}

		info.Environment["LD_PRELOAD"] = string.IsNullOrEmpty( existing ) ? bundled : $"{bundled}:{existing}";
		info.Environment[PreloadGuardVariable] = "1";

		// Only xcb ships in qt5_plugins/platforms, so a Wayland session has to go through XWayland.
		if ( string.IsNullOrEmpty( Environment.GetEnvironmentVariable( "QT_QPA_PLATFORM" ) ) )
		{
			info.Environment["QT_QPA_PLATFORM"] = "xcb";
		}

		try
		{
			using var child = Process.Start( info );
			if ( child is null ) return;
		}
		catch ( Exception e )
		{
			// Carry on in this process rather than failing to start at all - it may still work if the
			// host harfbuzz happens to be close enough to the vendored one.
			Console.Error.WriteLine( $"Could not re-exec with LD_PRELOAD, continuing without it: {e.Message}" );
			return;
		}

		Environment.Exit( 0 );
	}

	private static void UpdateNativeDllPath( string nativeDllPath )
	{
		// WARNING: this calls into Sandbox.Engine.dll - so we need to put it in
		// this method, which is executed AFTER CurrentDomain_AssemblyResolve is set
		// so that managed can find the correct dll
		NetCore.NativeDllPath = nativeDllPath;

		//
		// Put our native dll path first so that when looking up native dlls we'll
		// always use the ones from our folder first
		//
		if ( OperatingSystem.IsWindows() )
		{
			var path = System.Environment.GetEnvironmentVariable( "PATH" );
			path = $"{nativeDllPath};{path}";
			System.Environment.SetEnvironmentVariable( "PATH", path );
		}
	}

	private static Assembly CurrentDomain_AssemblyResolve( object sender, ResolveEventArgs args )
	{
		var trim = args.Name.Split( ',' )[0];

		var name = $"{ManagedDllPath}/{trim}.dll";

		// dlls with resources inside appear as a different name
		name = name.Replace( ".resources.dll", ".dll" );

		if ( System.IO.File.Exists( name ) )
		{
			return Assembly.LoadFrom( name );
		}

		return null;
	}
}
