﻿namespace Xake

open System.IO
open System.Resources
open Xake

[<AutoOpen>]
module DotNetTaskTypes =

    // CSC task and related types
    type TargetType = |Auto |AppContainerExe |Exe |Library |Module |WinExe |WinmdObj
    type TargetPlatform = |AnyCpu |AnyCpu32Preferred |ARM | X64 | X86 |Itanium
    // see http://msdn.microsoft.com/en-us/library/78f4aasd.aspx
    // defines, optimize, warn, debug, platform

    // ResGen task and its settings
    type ResgenSettingsType = {

        Resources: ResourceFileset list
        TargetDir: DirectoryInfo
        UseSourcePath: bool

        // TODO single file mode
        // TODO extra command-line args
    }

    type MsbVerbosity = | Quiet | Minimal | Normal | Detailed | Diag


[<AutoOpen>]
module DotnetTasks =

    module internal Impl =
        begin
        /// Escapes argument according to CSC.exe rules (see http://msdn.microsoft.com/en-us/library/78f4aasd.aspx)
        let escapeArgument (str:string) =
            let escape c s =
                match c,s with
                | '"',  (b,    str) -> (true,  '\\' :: '\"' ::    str)
                | '\\', (true, str) -> (true,  '\\' :: '\\' :: str)
                | '\\', (false,str) -> (false, '\\' :: str)
                | c,    (b,    str) -> (false, c :: str)

            if str |> String.exists (fun c -> c = '"' || c = ' ') then
                let ca = str.ToCharArray()
                let res = Array.foldBack escape ca (true,['"'])
                "\"" + System.String(res |> snd |> List.toArray)
            else
                str

        let isEmpty str = System.String.IsNullOrWhiteSpace(str)

        /// Gets the path relative to specified root path
        let getRelative (root:string) (path:string) =

            // TODO reimplement and test

            if isEmpty root then path
            elif path.ToLowerInvariant().StartsWith (root.ToLowerInvariant()) then
                // cut the trailing "\"
                let d = if root.Length < path.Length then 1 else 0
                path.Substring(root.Length + d)
            else
                path

        /// Makes resource name given the file name
        let makeResourceName (options:ResourceSetOptions) baseDir resxfile =

            let baseName = Path.GetFileName(resxfile)

            let baseName =
                match options.DynamicPrefix,baseDir with
                | true, Some dir ->
                    let path = Path.GetDirectoryName(resxfile) |> getRelative (Path.GetFullPath(dir))
                    if not <| isEmpty path then
                        path.Replace(Path.DirectorySeparatorChar, '.').Replace(':', '.') + "." + baseName
                    else
                        baseName
                | _ ->
                    baseName

            match options.Prefix with
                | Some prefix -> prefix + "." + baseName
                | _ -> baseName

        let compileResx (resxfile:File) (rcfile:File) =
            use resxreader = new System.Resources.ResXResourceReader (resxfile.FullName)
            resxreader.BasePath <- File.getDirName resxfile

            use writer = new ResourceWriter (rcfile.FullName)

            // TODO here we have deal with types somehow because we are running conversion under framework 4.5 but target could be 2.0
            writer.TypeNameConverter <-
                fun(t:System.Type) ->
                    t.AssemblyQualifiedName.Replace("4.0.0.0", "2.0.0.0")

            let reader = resxreader.GetEnumerator()
            while reader.MoveNext() do
                writer.AddResource (reader.Key :?> string, reader.Value)
            writer.Generate()

        let collectResInfo pathRoot = function
            |ResourceFileset (o,Fileset (fo,fs)) ->
                let mapFile file =
                    let resname = makeResourceName o fo.BaseDir (File.getFullName file) in
                    (resname,file)

                let (Filelist l) = Fileset (fo,fs) |> (toFileList pathRoot) in
                l |> List.map mapFile

        let endsWith e (str:string) = str.EndsWith (e, System.StringComparison.OrdinalIgnoreCase)
        let (|EndsWith|_|) e str = if endsWith e str then Some () else None

        let compileResxFiles = function
            | (res,(file:File)) when file |> File.getFileName |> endsWith ".resx" ->
                let tempfile = Path.GetTempFileName() |> File.make
                do compileResx file tempfile
                (Path.ChangeExtension(res,".resources"),tempfile,true)
            | (res,file) ->
                (res,file,false)

        let resolveTarget  =
            function
            | EndsWith ".dll" -> Library
            | EndsWith ".exe" -> Exe
            | _ -> Library

        let rec targetStr fileName = function
            |AppContainerExe -> "appcontainerexe" |Exe -> "exe" |Library -> "library" |Module -> "module" |WinExe -> "winexe" |WinmdObj -> "winmdobj"
            |Auto -> fileName |> resolveTarget |> targetStr fileName

        let platformStr = function
            |AnyCpu -> "anycpu" |AnyCpu32Preferred -> "anycpu32preferred" |ARM -> "arm" | X64 -> "x64" | X86 -> "x86" |Itanium -> "itanium"

        /// Parses the compiler output and returns messageLevel
        let levelFromString defaultLevel (text:string) :Level =
            if text.IndexOf "): warning " > 0 then Level.Warning
            else if text.IndexOf "): error " > 0 then Level.Error
            else defaultLevel
        let inline coalesce ls = //: 'a option list -> 'a option =
            ls |> List.fold (fun r a -> if Option.isSome r then r else a) None

        end // end of Impl module

    /// Generates binary resource files from resx, txt etc

    let ResgenSettings = {
        Resources = [Empty]
        TargetDir = System.IO.DirectoryInfo "."
        UseSourcePath = true
    }

    let ResGen (settings:ResgenSettingsType) =

        // TODO rewrite everything, it's just demo code
        let resgen baseDir (options:ResourceSetOptions) (resxfile:string) =
            use resxreader = new System.Resources.ResXResourceReader (resxfile)

            if settings.UseSourcePath then
                resxreader.BasePath <- Path.GetDirectoryName (resxfile)

            let rcfile =
                Path.Combine(
                    settings.TargetDir.FullName,
                    Path.ChangeExtension(resxfile, ".resource") |> Impl.makeResourceName options baseDir)

            use writer = new ResourceWriter (rcfile)

            let reader = resxreader.GetEnumerator()
            while reader.MoveNext() do
                writer.AddResource (reader.Key :?> string, reader.Value)

            rcfile

        action {
            for r in settings.Resources do
                let (ResourceFileset (settings,fileset)) = r
                let (Fileset (options,_)) = fileset
                let! (Filelist files) = getFiles fileset

                do files |> List.map (File.getFullName >> resgen options.BaseDir settings) |> ignore
            ()
        }

