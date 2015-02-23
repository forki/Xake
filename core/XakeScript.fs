﻿namespace Xake

[<AutoOpen>]
module XakeScript =

    open BuildLog
    open System.Threading

    type XakeOptionsType = {
        /// Defines project root folder
        ProjectRoot : string    // TODO DirectoryInfo?
        /// Maximum number of rules processed simultaneously.
        Threads: int

        /// custom logger
        CustomLogger: ILogger

        /// Log file and verbosity level.
        FileLog: string
        FileLogLevel: Verbosity

        /// Console output verbosity level. Default is Warn
        ConLogLevel: Verbosity
        /// Overrides "want", i.e. target list
        Want: string list
        Vars: (string * string) list

        /// Defines whether `run` should throw exception if script fails
        FailOnError: bool
    }


    type Rule<'ctx> = 
            | FileRule of string * (Artifact -> Action<'ctx,unit>)
            | PhonyRule of string * Action<'ctx,unit>
            | FileConditionRule of (string -> bool) * (Artifact -> Action<'ctx,unit>)
    type Rules<'ctx> = Rules of Rule<'ctx> list

    type ExecStatus = | Succeed | Skipped | JustFile
    type TaskPool = Agent<WorkerPool.ExecMessage<ExecStatus>>

    type ExecContext = {
        TaskPool: TaskPool
        Db: Agent<Storage.DatabaseApi>
        Throttler: SemaphoreSlim
        Options: XakeOptionsType
        Rules: Rules<ExecContext>
        Logger: ILogger
        RootLogger: ILogger
        Ordinal: int
    }

    /// Main type.
    type XakeScript = XakeScript of XakeOptionsType * Rules<ExecContext>

    /// <summary>
    /// Dependency state.
    /// </summary>
    type DepState =
        | NotChanged
        | Depends of Target * DepState list
        | Other of string

    /// Default options
    let XakeOptions = {
        ProjectRoot = System.IO.Directory.GetCurrentDirectory()
        Threads = System.Environment.ProcessorCount
        ConLogLevel = Normal

        CustomLogger = CustomLogger (fun _ -> false) ignore
        FileLog = "build.log"
        FileLogLevel = Chatty
        Want = []
        FailOnError = false
        Vars = List<string*string>.Empty
        }

    module private Impl =
        open WorkerPool
        open BuildLog
        open Storage

        let nullableToOption = function | null -> None | s -> Some s
        let valueByName variableName = function |name,value when name = variableName -> Some value | _ -> None

        let TimeCompareToleranceMs = 100.0

        /// Writes the message with formatting to a log
        let writeLog (level:Logging.Level) fmt    =
            let write s = action {
                let! (ctx:ExecContext) = getCtx()
                return ctx.Logger.Log level "%s" s
            }
            Printf.kprintf write fmt

        let addRule rule (Rules rules) :Rules<_> =    Rules (rule :: rules)

        let getEnvVar = System.Environment.GetEnvironmentVariable >> nullableToOption
        let getVar ctx name = ctx.Options.Vars |> List.tryPick (valueByName name)

        // Ordinal of the task being added to a task pool
        let refTaskOrdinal = ref 0

        // locates the rule
        let private locateRule (Rules rules) projectRoot target =
            let matchRule rule = 
                match rule, target with
                    |FileConditionRule (f,_), FileTarget file when (f file.FullName) = true ->
                        //writeLog Level.Debug "Found conditional pattern '%s'" name
                        Some (rule)
                    |FileRule (pattern,_), FileTarget file when Fileset.matches pattern projectRoot file.FullName ->
                        // writeLog Verbose "Found pattern '%s' for %s" pattern (getShortname target)
                        Some (rule)
                    |PhonyRule (name,_), PhonyAction phony when phony = name ->
                        // writeLog Verbose "Found phony pattern '%s'" name
                        Some (rule)
                    | _ -> None
                
            rules |> List.tryPick matchRule

        let private reportError ctx error details =
            do ctx.Logger.Log Error "Error '%s'. See build.log for details" error
            do ctx.Logger.Log Verbose "Error details are:\n%A\n\n" details

        let private raiseError ctx error details =
            do reportError ctx error details
            raise (XakeException(sprintf "Script failed (error code: %A)\n%A" error details))

        /// <summary>
        /// Creates a context for a new task
        /// </summary>
        let newTaskContext ctx =
            let ordinal = System.Threading.Interlocked.Increment(refTaskOrdinal)
            let prefix = ordinal |> sprintf "%i> "
            in
            {ctx with Ordinal = ordinal; Logger = PrefixLogger prefix ctx.RootLogger}

        /// Gets single dependency state
        let getDepState getVar getFileList (isOutdatedTarget: Target -> DepState list) = function
            | File (a:Artifact, wrtime) when not(a.Exists && abs((a.LastWriteTime - wrtime).TotalMilliseconds) < TimeCompareToleranceMs) ->
                DepState.Other <| (sprintf "removed or changed file '%s'" a.Name)

            | ArtifactDep (FileTarget file) when not file.Exists ->
                DepState.Other <| sprintf "target doesn't exist '%s'" file.Name

            | ArtifactDep dependeeTarget ->
                let ls = isOutdatedTarget dependeeTarget in

                if List.exists ((<>) DepState.NotChanged) ls then
                    DepState.Depends (dependeeTarget,ls)
                else
                    NotChanged
    
            | EnvVar (name,value) when value <> getEnvVar name ->
                DepState.Other <| sprintf "Environment variable %s was changed from '%A' to '%A'" name value (getEnvVar name)

            | Var (name,value) when value <> getVar name ->
                DepState.Other <| sprintf "Global script variable %s was changed '%A'->'%A'" name value (getVar name)

            | AlwaysRerun ->
                DepState.Other <| "alwaysRerun rule"

            | GetFiles (fileset,files) ->                
                let newfiles = getFileList fileset
                let diff = compareFileList files newfiles

                if List.isEmpty diff then
                    NotChanged
                else
                    Other <| sprintf "File list is changed for fileset %A: %A" fileset diff
            | _ -> NotChanged

        /// <summary>
        /// Gets all changed dependencies
        /// </summary>
        /// <param name="ctx"></param>
        /// <param name="getDeps">gets state for nested dependency</param>
        /// <param name="tgt"></param>
        let getDepsImpl ctx getDeps tgt =

            let lastBuild = (fun ch -> GetResult(tgt, ch)) |> ctx.Db.PostAndReply
            let dep_state = getDepState (getVar ctx) (toFileList ctx.Options.ProjectRoot) (getDeps)

            match lastBuild with
            | Some {BuildResult.Depends = []} ->
                [DepState.Other "No dependencies"]

            | Some {BuildResult.Result = FileTarget file} when not file.Exists ->
                [DepState.Other "target not found"]

            | Some {BuildResult.Depends = depends} ->
                depends |> List.map dep_state |> List.filter ((<>) DepState.NotChanged)

            | _ ->
                [DepState.Other "Unknown state"]
        
        /// <summary>
        /// Gets dependencies for specific target
        /// </summary>
        /// <param name="ctx"></param>
        let getDeps (ctx:ExecContext) =
            let rec mg = Common.memoize (getDepsImpl ctx (fun x -> mg x))
            in mg

        /// <summary>
        /// Gets dependencies for specific target
        /// </summary>
        /// <param name="ctx"></param>
        let rec getDepsSlow (ctx:ExecContext) tgt =
            // the commented out code is very slow too
            //let rec mg target = Common.memoize (getDepsImpl ctx mg) target
            //in mg
            getDepsImpl ctx (getDepsSlow ctx) tgt
            

        /// Gets true if rebuild is required
        let rec needRebuild ctx (tgt:Target) =

            let mapResultBack = function
                | NotChanged -> false, ""
                | DepState.Other reason -> true, reason
                | DepState.Depends (t,_) -> true, "Depends on " + (Target.getFullName t)

            let isOutdated = getDepState (getVar ctx) (toFileList ctx.Options.ProjectRoot) (fun _ -> [DepState.NotChanged]) >> mapResultBack

            let replyYes reason =
                do ctx.Logger.Log Info "Rebuild %A: %s" (getShortname tgt) reason
                async {return true}

            function
            | Some {BuildResult.Depends = []} ->
                replyYes "No dependencies"

            | Some {BuildResult.Result = FileTarget file} when not file.Exists ->
                replyYes "target not found"

            | Some result ->
                let artifactDeps, immediateDeps = result.Depends |> List.partition (function |ArtifactDep (FileTarget file) when file.Exists -> true | _ -> false)

                match immediateDeps |> List.tryFind (isOutdated >> fst) with
                | Some d ->
                    let _,reason = isOutdated d in
                    replyYes reason
                | _ ->
                    let targets = artifactDeps |> List.map (function |ArtifactDep dep -> dep) in
                    async {
                        // ISSUE executes the task despite the name (should just request the status)
                        let! status = execNeed ctx targets
                        return fst status = ExecStatus.Succeed
                    }

            | _ -> replyYes "reason unknown (new file&)"

        // executes single artifact
        and private execOne ctx target =

            let run action chnl =
                Run(target,
                    async {
                        let! lastBuild = (fun ch -> GetResult(target, ch)) |> ctx.Db.PostAndAsyncReply

                        let! willRebuild = needRebuild ctx target lastBuild

                        if willRebuild then 
                            let taskContext = newTaskContext ctx                           
                            do ctx.Logger.Log Command "Started %s as task %i" (getShortname target) taskContext.Ordinal

                            let! (result,_) = action (BuildLog.makeResult target,taskContext)
                            Store result |> ctx.Db.Post

                            do ctx.Logger.Log Command "Completed %s" (getShortname target)
                            return ExecStatus.Succeed
                        else
                            do ctx.Logger.Log Command "Skipped %s (up to date)" (getShortname target)
                            return ExecStatus.Skipped
                    }, chnl)

            let actionFromRule = function
                | FileRule (_, action)
                | FileConditionRule (_, action) ->
                    let (FileTarget artifact) = target in
                    let (Action r) = action artifact in
                    Some r
                | PhonyRule (_, Action r) -> Some r

            // result expression is...
            target
            |> locateRule ctx.Rules ctx.Options.ProjectRoot
            |> Option.bind actionFromRule
            |> function
                | Some action ->
                    async {
                        let! waitTask = run action |> ctx.TaskPool.PostAndAsyncReply
                        let! status = waitTask
                        return status, Dependency.ArtifactDep target
                    }
                | None ->
                    match target with
                    | FileTarget file when file.Exists ->
                        async {return ExecStatus.JustFile, Dependency.File (file, file.LastWriteTime)}
                    | _ -> raiseError ctx (sprintf "Neither rule nor file is found for '%s'" (getFullname target)) ""

        /// <summary>
        /// Executes several artifacts in parallel.
        /// </summary>
        and private execMany ctx = Seq.ofList >> Seq.map (execOne ctx) >> Async.Parallel

        /// <summary>
        /// Gets the status of dependency artifacts (obtained from 'need' calls).
        /// </summary>
        /// <returns>
        /// ExecStatus.Succeed,... in case at least one dependency was rebuilt
        /// </returns>
        and execNeed ctx targets : Async<ExecStatus * Dependency list> =
            async {
                ctx.Throttler.Release() |> ignore
                let! statuses = targets |> execMany ctx
                do! ctx.Throttler.WaitAsync(-1) |> Async.AwaitTask |> Async.Ignore

                let dependencies = statuses |> Array.map snd |> List.ofArray in

                return statuses
                |> Array.exists (fst >> (=) ExecStatus.Succeed)
                |> function
                    | true -> ExecStatus.Succeed,dependencies
                    | false -> ExecStatus.Skipped,dependencies
            }

        /// <summary>
        /// phony actions are detected by their name so if there's "clean" phony and file "clean" in `need` list if will choose first
        /// </summary>
        /// <param name="ctx"></param>
        /// <param name="name"></param>
        let makeTarget ctx name =
            let (Rules rr) = ctx.Rules
            if rr |> List.exists (function |PhonyRule (n,_) when n = name -> true | _ -> false) then
                PhonyAction name
            else
                FileTarget (Artifact (ctx.Options.ProjectRoot </> name))        

        /// Executes the build script
        let run script =

            let (XakeScript (options,rules)) = script
            let logger = CombineLogger (ConsoleLogger options.ConLogLevel) options.CustomLogger

            let logger =
                match options.FileLog, options.FileLogLevel with
                | null,_ | "",_
                | _,Verbosity.Silent -> logger
                | logFileName,level -> CombineLogger logger (FileLogger logFileName level)

            let (throttler, pool) = WorkerPool.create logger options.Threads

            let start = System.DateTime.Now
            let db = Storage.openDb options.ProjectRoot logger
            let ctx = {Ordinal = 0; TaskPool = pool; Throttler = throttler; Options = options; Rules = rules; Logger = logger; RootLogger = logger; Db = db }

            logger.Log Info "Options: %A" options

            let rec unwindAggEx (e:System.Exception) = seq {
                match e with
                    | :? System.AggregateException as a -> yield! a.InnerExceptions |> Seq.collect unwindAggEx
                    | a -> yield a
                }

            try
                try
                    let checkEmpty = function
                    | [] ->
                        logger.Log Level.Message "No target(s) specified. Defaulting to 'main'"
                        ["main"]
                    | targets -> targets

                    options.Want |> checkEmpty |> (List.map (makeTarget ctx) >> execMany ctx >> Async.RunSynchronously >> ignore)
                    logger.Log Message "\n\n\tBuild completed in %A\n" (System.DateTime.Now - start)
                with 
                    | exn ->
                        let th = if options.FailOnError then raiseError else reportError
                        let errors = exn |> unwindAggEx |> Seq.map (fun e -> e.Message) in
                        th ctx (exn.Message + "\n" + (errors |> String.concat "\r\n            ")) exn
                        logger.Log Message "\n\n\tBuild failed after running for %A\n" (System.DateTime.Now - start)
            finally
                db.PostAndReply Storage.CloseWait

    /// Creates the rule for specified file pattern.    
    let ( *> ) pattern fnRule = FileRule (pattern, fnRule)
    let ( *?> ) fn fnRule = FileConditionRule (fn, fnRule)

    /// Creates phony action (check if I can unify the operator name)
    let (=>) name fnRule = PhonyRule (name,fnRule)

    /// Script builder.
    type RulesBuilder(options) =

        let updRules (XakeScript (options,rules)) f = XakeScript (options, f(rules))
        let updTargets (XakeScript (options,rules)) f = XakeScript ({options with Want = f(options.Want)}, rules)

        member o.Bind(x,f) = f x
        member o.Zero() = XakeScript (options, Rules [])
        member o.Yield(())    = o.Zero()

        member this.Run(script) = Impl.run script
            
        [<CustomOperation("rule")>] member this.Rule(script, rule)                  = updRules script (Impl.addRule rule)
        [<CustomOperation("addRule")>] member this.AddRule(script, pattern, action) = updRules script (pattern *> action |> Impl.addRule)
        [<CustomOperation("phony")>] member this.Phony(script, name, action)        = updRules script (name => action |> Impl.addRule)
        [<CustomOperation("rules")>] member this.Rules(script, rules)               = (rules |> List.map Impl.addRule |> List.fold (>>) id) |> updRules script

        [<CustomOperation("want")>] member this.Want(script, targets)               = updTargets script (function |[] -> targets | _ as x -> x)    // Options override script!
        [<CustomOperation("wantOverride")>] member this.WantOverride(script,targets)= updTargets script (fun _ -> targets)

    /// creates xake build script
    let xake options =
        new RulesBuilder(options)

    /// Create xake build script using command-line arguments to define script options
    let xakeArgs args options =
        let _::targets = Array.toList args
        // this is very basic implementation which only recognizes target names
        // TODO support global variables (with dependency tracking)
        // TODO support sequential/parallel runs e.g. "clean release-build;debug-build"
        new RulesBuilder({options with Want = targets})

    /// Gets the script options.
    let getCtxOptions() = action {
        let! (ctx: ExecContext) = getCtx()
        return ctx.Options
    }

    /// key functions implementation

    let private needImpl targets =
            action {
                let! ctx = getCtx()
                let! _,deps = targets |> Impl.execNeed ctx

                let! result = getResult()
                do! setResult {result with Depends = result.Depends @ deps}
            }

    /// Executes and awaits specified artifacts
    let need targets =
            action {
                let! ctx = getCtx()
                let t' = targets |> (List.map (Impl.makeTarget ctx))

                do! needImpl t'
            }

    let needFiles (Filelist files) =
            action {
                let! ctx = getCtx()
                let targets = files |> List.map (fun f -> new Artifact (f.FullName) |> FileTarget)

                do! needImpl targets
         }

    /// Instructs Xake to rebuild the target even if dependencies are not changed
    let alwaysRerun () = action {
        let! ctx = getCtx()
        let! result = getResult()
        do! setResult {result with Depends = Dependency.AlwaysRerun :: result.Depends}
    }

    /// Gets the environment variable
    let getEnv variableName = action {
        let! ctx = getCtx()

        let value = Impl.getEnvVar variableName

        // record the dependency
        let! result = getResult()
        do! setResult {result with Depends = Dependency.EnvVar (variableName,value) :: result.Depends}

        return value
    }

    /// Gets the global variable
    let getVar variableName = action {
        let! ctx = getCtx()
        let value = ctx.Options.Vars |> List.tryPick (Impl.valueByName variableName)
        
        // record the dependency
        let! result = getResult()
        do! setResult {result with Depends = Dependency.Var (variableName,value) :: result.Depends}

        return value
    }

    /// Executes and awaits specified artifacts
    let getFiles fileset = action {
        let! ctx = getCtx()
        let files = fileset |> toFileList ctx.Options.ProjectRoot

        let! result = getResult()
        do! setResult {result with Depends = result.Depends @ [Dependency.GetFiles (fileset,files)]}

        return files
    }

    /// Writes a message to a log
    let writeLog = Impl.writeLog

    /// <summary>
    /// Gets state of particular target
    /// </summary>
    let getDirtyState = Impl.getDeps
    let getDirtyStateSlow = Impl.getDepsSlow

    /// Defined a rule that demands specified targets
    /// e.g. "main" ==> ["build-release"; "build-debug"; "unit-test"]
    let (<==) name targets = PhonyRule (name,action {
        do! need targets
        do! alwaysRerun()   // always check demanded dependencies. Otherwise it wan't check any target is available
    })
    let (==>) = (<==)
