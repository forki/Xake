// boostrapping xake.core
System.Environment.CurrentDirectory <- __SOURCE_DIRECTORY__

let file = System.IO.Path.Combine("packages", "Xake.Core.dll")
if not (System.IO.File.Exists file) then
    printf "downloading xake.core assembly..."; System.IO.Directory.CreateDirectory("packages") |> ignore
    let url = "https://github.com/OlegZee/Xake/releases/download/v0.2.0/Xake.Core.dll"
    use wc = new System.Net.WebClient() in wc.DownloadFile(url, file + "__"); System.IO.File.Move(file + "__", file)
    printfn ""

// xake build file body
#r @"../bin/Xake.Core.dll"

open Xake
open Xake.Tasks.Dotnet

do xake {ExecOptions.Default with FileLog = "build.log"; Threads = 4 } {

  rule ("main" ==> ["helloworld.exe"])

  rule("*.exe" ..> Csc {
      CscSettings with
        Src = !! "helloworld.cs"
      })
}
