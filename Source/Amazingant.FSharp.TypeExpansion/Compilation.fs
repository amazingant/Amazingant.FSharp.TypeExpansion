namespace Amazingant.FSharp.TypeExpansion

open Amazingant.FSharp.TypeExpansion.Attributes

open System
open System.IO
open System.Reflection
open Microsoft.FSharp.Compiler


[<AutoOpen>]
module internal Compilation =
    let notEmpty = Seq.isEmpty >> not
    let joinLines    (x : string seq) = String.Join("\n"  , x)
    let joinTwoLines (x : string seq) = String.Join("\n\n", x)
    let fileNotExist = File.Exists >> not
    let splitCommas (x : obj) = (x :?> string).Split ([|','|], StringSplitOptions.RemoveEmptyEntries) |> Array.toList
    let buildRefs = Seq.collect (fun x -> ["-r";x]) >> Seq.toList
    let switch f x y = f y x
    // Uses F#'s static type constraint feature to check for a specified
    // attribute type on anything that has the GetCustomAttributes function;
    // e.g. System.Type, System.Reflection.MethodInfo, etc.
    let inline hasAttribute< ^R when ^R : (member GetCustomAttributes : Type -> bool -> obj [])>
        (a : Type) (r : ^R) =
            (^R : (member GetCustomAttributes : Type -> bool -> obj []) (r, a, false))
            |> notEmpty


    // Helper type for processing the user-specified source path
    type internal CompileSource (path : string, omitFile : string) =
        let (|Project|List|File|) (file : string) =
            let isProj = file.EndsWith ".fsproj"
            let isList = file.Contains ","
            let isFile = (not <| file.Contains ",") && (file.EndsWith ".fsx" || file.EndsWith ".fs")
            match isProj, isList, isFile with
            |  true, false, false -> Project file
            | false,  true, false -> List (splitCommas file |> List.filter (fun x -> x <> omitFile))
            | false, false,  true -> File file
            | _ ->
                failwithf "Provided source path does not appear to be valid; should be a project file, a source file, or a comma-separated list of paths"

        member __.Files : string list =
            match path with
            | File x -> [x]
            | List xs -> xs
            | Project x ->
                // TODO: Load XML and find source files? Exclude current file somehow?
                [x]


    // This finds a copy of FSharp.Core that has the required optdata and
    // sigdata files, as well as the locations of this assembly and mscorlib.
    // These three base references are required for all of the compiling done.
    let requiredRefs =
        let fsCoreMain = @"C:\Program Files\Reference Assemblies\Microsoft\FSharp\.NETFramework\v4.0\4.4.0.0\FSharp.Core.dll"
        let fsCorex86 = @"C:\Program Files (x86)\Reference Assemblies\Microsoft\FSharp\.NETFramework\v4.0\4.4.0.0\FSharp.Core.dll"
        let fsCore =
            if File.Exists fsCoreMain then
                fsCoreMain
            elif File.Exists fsCorex86 then
                fsCorex86
            else
                failwithf "Version 4.0 of F# does not appear to be installed on this system"
        [
            typeof<CompileSource>.Assembly.Location;
            typeof<string>.Assembly.Location;
            fsCore;
        ]


    // Filters given errors to ignore warnings, throws exception if proper
    // errors exist
    let handleCompilerErrors (errors : FSharpErrorInfo seq) =
        // Gather any compilation errors
        let msg =
            errors
            |> Seq.filter (fun x -> x.Severity = FSharpErrorSeverity.Error)
            |> Seq.map
                (fun x ->
                    sprintf "%s (%i,%i)-(%i,%i) %s"
                        x.FileName
                        x.StartLineAlternate
                        x.StartColumn
                        x.EndLineAlternate
                        x.EndColumn
                        x.Message
                    )
            |> joinLines
        // If there were any errors, throw them
        match msg.Trim() with
        | null | "" -> ()
        | x -> failwithf "Encountered errors while compiling source:\n%s" x


    // Builds and returns a dynamic assembly from the given source
    let dynamicBuild (source : CompileSource) refs =
        // Compile and gather the results
        let (errors, _, assembly) =
            try
                let args = [ "fsc.exe"; "--noframework"; "-a"; ]
                let refs = buildRefs (requiredRefs@refs)
                let args = args @ refs @ source.Files |> Seq.toArray
                let compiler = SimpleSourceCodeServices.SimpleSourceCodeServices()
                compiler.CompileToDynamicAssembly(args, None)
            with
            | ex ->
                // If the compiler throws an exception, add an extra message
                failwithf "Internal compiler error:\n%A" ex

        // Throw an exception if there were compilation errors
        handleCompilerErrors errors
        // If compilation succeeded, the assembly should have been returned
        match assembly with
        | None -> failwithf "Could not compile source"
        | Some x -> x


    // Checks the given method to see if it can be used for expansion
    let isValidExpander (mi : MethodInfo) =
        let ps = mi.GetParameters()
        // Must have the TypeExpander attribute
        mi |> hasAttribute typeof<TypeExpanderAttribute> &&
        // Must return a string
        mi.ReturnType = typeof<string> &&
        // Must take exactly one parameter
        ps.Length = 1 &&
        // Parameter must be of type System.Type
        ps.[0].ParameterType = typeof<Type>


    // Processes the given source, passes types through expanders, and returns
    // the expanded source code
    let processFiles source refs =
        let asm = dynamicBuild source refs
        // Get the types that can be expanded
        let targets =
            asm.DefinedTypes
            |> Seq.filter (hasAttribute typeof<ExpandableTypeAttribute>)
            |> Seq.toArray
        // Get the functions that can perform expansion
        let fs =
            let flags =
                BindingFlags.Public       |||
                BindingFlags.Static       |||
                BindingFlags.DeclaredOnly
            asm.DefinedTypes
            |> Seq.collect (fun x -> x.GetMethods(flags))
            |> Seq.filter isValidExpander

        // For each function, collect the expanded version of every target type,
        // filter out any results that are empty or null, and concatenate the
        // results together with some empty space
        fs
        |> Seq.collect
            (fun x ->
                let xAttr : TypeExpanderAttribute = x.GetCustomAttribute()
                targets
                |> Seq.map
                    (fun y ->
                        let yAttr : ExpandableTypeAttribute = y.GetCustomAttribute()
                        if yAttr.CanUseTemplate(xAttr.Name, xAttr.RequireExplicitUse) then
                            x.Invoke(null, [|y|]) :?> string
                        else
                            ""
                    )
            )
        |> Seq.filter (String.IsNullOrWhiteSpace >> not)
        |> joinTwoLines


    let buildStaticParameter<'t> name (defaultValue : 't option) xmlDoc =
        // TODO: Handle XML documentation
        let def = box <| defaultArg defaultValue Unchecked.defaultof<'t>
        {
            new ParameterInfo() with
                override __.Name            with get () = name
                override __.ParameterType   with get () = typeof<'t>
                override __.Position        with get () = 0
                override __.RawDefaultValue with get () = def
                override __.DefaultValue    with get () = def
                override __.Attributes
                    with get () =
                        match defaultValue with
                        | Some _ -> ParameterAttributes.Optional
                        | _ -> ParameterAttributes.None
        }