﻿namespace Xake

[<AutoOpen>]
module XakeScript =
  open System.Threading

  type XakeOptionsType = {
    /// Defines project root folder
    ProjectRoot : string
    /// Maximum number of threads to run the rules
    Threads: int

    /// Log file and verbosity level
    FileLog: string
    FileLogLevel: Level

    /// Console output verbosity level. Default is Warn
    ConLogLevel: Level
    /// Overrides "want", i.e. target list
    Want: string list
  }


  type RuleTarget =
      | FilePattern of string | PhonyTarget of string
  type Rule<'ctx> = 
      | FileRule of string * (Target -> Action<'ctx,unit>)
      | PhonyRule of string * Action<'ctx,unit>
  type Rules<'ctx> = Rules of Map<RuleTarget, Rule<'ctx>>

  type ExecContext = {
    TaskPool: MailboxProcessor<WorkerPool.ExecMessage>
    Throttler: SemaphoreSlim
    Options: XakeOptionsType
    Rules: Rules<ExecContext>
  }

  /// Main type.
  type XakeScript = XakeScript of XakeOptionsType * Rules<ExecContext>

  /// Default options
  let XakeOptions = {
    ProjectRoot = System.IO.Directory.GetCurrentDirectory()
    Threads = 4
    ConLogLevel = Level.Warning

    FileLog = ""
    FileLogLevel = Level.Error
    Want = []
    }

  module private Impl =
    open WorkerPool

    let makeFileRule  pattern action = FileRule (pattern, action)
    let makePhonyRule name action = PhonyRule (name, action)

    let addRule rule (Rules rules) :Rules<_> = 
      let target = match rule with
        | FileRule (selector,_) -> (FilePattern selector)
        | PhonyRule (name,_) -> (PhonyTarget name)
      rules |> Map.add target rule |> Rules

    // locates the rule
    let private locateRule (Rules rules) projectRoot target =
      let matchRule ruleTarget b = 
        match ruleTarget, target with
          |FilePattern pattern, FileTarget file when Fileset.matches pattern projectRoot file.FullName ->
              Logging.log Verbose "Found pattern '%s' for %s" pattern (getShortname target)
              Some (b)
          |PhonyTarget name, PhonyAction phony when phony = name ->
              Logging.log Verbose "Found phony pattern '%s'" name
              Some (b)
          | _ -> None
      rules |> Map.tryPick matchRule

    // executes single artifact
    let private execOne ctx target =
      let action =
        locateRule ctx.Rules ctx.Options.ProjectRoot target |>
        Option.bind (function
          | FileRule (_, action) -> let (Action r) = action target in Some r
          | PhonyRule (_, Action r) -> Some r)

      match action with
      | Some action ->
        async {
          let! task = ctx.TaskPool.PostAndAsyncReply(fun chnl -> Run(target, action ctx, chnl))
          return! task
        }
      | None ->   // TODO should always fail for phony
        if not <| exists target then exitWithError 2 (sprintf "Neither rule nor file is found for '%s'" (getFullname target)) ""
        async {()}


    /// Executes several artifacts in parallel
    let private exec ctx = Seq.ofList >> Seq.map (execOne ctx) >> Async.Parallel

    let private dumpTarget = function
      | FileTarget f -> "file " + f.Name
      | PhonyAction a -> "action " + a

    /// Executes and awaits specified artifacts
    let needTarget targets = action {

        //log Level.Info "targets %A" (targets |> List.map dumpTarget)

        let! ctx = getCtx
        ctx.Throttler.Release() |> ignore
        do! targets |> (exec ctx >> Async.Ignore)
        do! ctx.Throttler.WaitAsync(-1) |> Async.AwaitTask |> Async.Ignore
      }

    /// Executes and awaits specified artifacts
    let needFileset fileset =
      action {
        let! ctx = getCtx
        do! fileset |> (toFileList ctx.Options.ProjectRoot >> List.map FileTarget) |> needTarget
      }
      
    /// Executes and awaits specified artifacts
    let need targets =
      action {
        let! ctx = getCtx
        let (Rules rules) = ctx.Rules
        let isPhony s = rules |> Map.containsKey(PhonyTarget s)

        // phony actions are detected by their name so if there's "clean" phony and file "clean" in `need` list if will choose first
        let mf name = match isPhony name with
          | true -> PhonyAction name
          | _ ->    FileTarget <| System.IO.FileInfo (ctx.Options.ProjectRoot </> name)
        do! targets |> (List.map mf) |> needTarget
      }

    /// Executes the build script
    let run script =

      let (XakeScript (options,rules)) = script
      let (throttler, pool) = WorkerPool.create options.Threads
      let ctx = {TaskPool = pool; Throttler = throttler; Options = options; Rules = rules}

      let (Rules rr) = rules
      let mapNameToTarget name =
        match rr.ContainsKey (PhonyTarget name) with
        | true -> PhonyAction name
        | false -> toFileTarget name

      try
        options.Want |> (List.map mapNameToTarget >> exec ctx >> Async.RunSynchronously >> ignore)
      with 
        | :? System.AggregateException as a ->
          let errors = a.InnerExceptions |> Seq.map (fun e -> e.Message) |> Seq.toArray
          exitWithError 255 (a.Message + "\n" + System.String.Join("\r\n      ", errors)) a
        | exn -> exitWithError 255 exn.Message exn

  /// Script builder.
  type RulesBuilder(options) =

    let updRules (XakeScript (options,rules)) f = XakeScript (options, f(rules))
    let updTargets (XakeScript (options,rules)) f = XakeScript ({options with Want = f(options.Want)}, rules)

    member o.Bind(x,f) = f x
    member o.Zero() = XakeScript (options, Rules Map.empty)
    member o.Yield(())  = o.Zero()

    member this.Run(script) =

      let start = System.DateTime.Now
      printfn "Options: %A" options
      Impl.run script
      printfn "\nBuild completed in %A" (System.DateTime.Now - start)
      ()

    [<CustomOperation("rule")>] member this.Rule(script, rule)                  = updRules script (Impl.addRule rule)
    [<CustomOperation("addRule")>] member this.AddRule(script, pattern, action) = updRules script (Impl.makeFileRule pattern action |> Impl.addRule)
    [<CustomOperation("phony")>] member this.Phony(script, name, action)        = updRules script (Impl.makePhonyRule name action |> Impl.addRule)
    [<CustomOperation("rules")>] member this.Rules(script, rules)               = (rules |> List.map Impl.addRule |> List.fold (>>) id) |> updRules script

    [<CustomOperation("want")>] member this.Want(script, targets)                = updTargets script (function |[] -> targets | _ as x -> x)  // Options override script!
    [<CustomOperation("wantOverride")>] member this.WantOverride(script,targets) = updTargets script (fun _ -> targets)

  /// creates xake build script
  let xake options = new RulesBuilder(options)

  /// key function implementation
  let needFileset = Impl.needFileset
  let needTgt = Impl.needTarget
  let need = Impl.need  // TODO one must stand

  /// Creates the rule for specified file pattern.  
  let ( *> ) = Impl.makeFileRule

  /// Creates phony action (check if I can unify the operator name)
  let (=>) = Impl.makePhonyRule

  // Helper method to obtain script options within rule/task implementation
  let getCtxOptions = action {
    let! (ctx: ExecContext) = getCtx
    return ctx.Options
  }