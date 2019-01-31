(**
# FsBlog Script

This script is the main workhorse of FsBlog that just coordinates the commands
and tasks that operate with the static site generation.
*)

#I @"packages/FAKE/tools/"
#I @"packages/FSharp.Configuration/lib/net45"
#I @"packages/RazorEngine/lib/net45"
#I @"packages/Suave/lib/net40"
#I @"bin/FsBlogLib"

#r "FakeLib.dll"
#r "RazorEngine.dll"
#r "FsBlogLib.dll"
#r "FSharp.Configuration.dll"
#r "Suave.dll"

open Fake
open Fake.Git
open System
open System.IO
open FsBlogLib
open FsBlogLib.FileHelpers
open FSharp.Configuration
open FSharp.Http

// --------------------------------------------------------------------------------------
// Configuration.
// --------------------------------------------------------------------------------------
Environment.CurrentDirectory <- __SOURCE_DIRECTORY__
type Config = YamlConfig<"config/config.yml">

let config = Config()
let root = config.url.AbsoluteUri
let title = config.title
let description = config.description
let gitLocation = config.gitlocation
let gitbranch = config.gitbranch

let source = __SOURCE_DIRECTORY__ ++ config.source
let blog = __SOURCE_DIRECTORY__ ++ config.blog
let blogIndex = __SOURCE_DIRECTORY__ ++ config.blogIndex
let beers = __SOURCE_DIRECTORY__ ++ config.beers
let beerIndex = __SOURCE_DIRECTORY__ ++ config.beerIndex
let themes = __SOURCE_DIRECTORY__ ++ config.themes
let content = __SOURCE_DIRECTORY__ ++ config.content
let layouts = content ++ config.layouts
let template = __SOURCE_DIRECTORY__ ++ config.template

let output = __SOURCE_DIRECTORY__ ++ config.output
let deploy = __SOURCE_DIRECTORY__ ++ config.deploy

let tagRenames = List.empty<string*string> |> dict
let exclude = []

let special =
    [ source ++ "index.cshtml"
      source ++ "blog" ++ "index.cshtml" ]
let rsscount = 20

// --------------------------------------------------------------------------------------
// Regenerates the site
// --------------------------------------------------------------------------------------

type RoutingMode =
    | Production
    | Preview

let buildSite routing updateTagArchive =

    let root =
        match routing with
        | Production -> config.url.AbsoluteUri
        | Preview -> "http://localhost:8080"

    let dependencies = [ yield! Directory.GetFiles(layouts) ]
    let noModel = { Blog.Root=root; Blog.Posts = [||]; Blog.MonthlyPosts=[||]; Blog.MonthlyBeerPosts=[||]; Blog.TaggedPosts=[||]; Blog.TaggedBeers=[||]; Blog.Beers=[||]; Blog.GenerateAll=true; Blog.Title=title; Blog.AllPosts=[||]; }
    let razor = FsBlogLib.Razor(layouts, Model = noModel)
    let model =  Blog.LoadModel(tagRenames, Blog.TransformAsTemp (template, source) razor, root, blog, beers, title)

    // Generate RSS feed
    Blog.GenerateRss root title description model rsscount (output ++ "feed.xml")
    
    Blog.GenerateTagListing layouts template model output

    let filesToProcess =
        FileHelpers.GetSourceFiles source output
        |> FileHelpers.SkipExcludedFiles exclude
        |> FileHelpers.TransformOutputFiles output source
        |> FileHelpers.FilterChangedFiles dependencies special

    let razor = FsBlogLib.Razor(layouts, Model = model)
    for current, target in filesToProcess do
        FileHelpers.EnsureDirectory(Path.GetDirectoryName(target))
        printfn "Processing file: %s" (current.Substring(source.Length))
        Blog.TransformFile template true razor None current target

    FileHelpers.CopyFiles (source ++ "assets") (output ++ "assets")

    Blog.GenerateSitemap (new Uri(root)) output (output ++ "sitemap.xml")

// --------------------------------------------------------------------------------------
// Static site tooling as a set of targets.
// --------------------------------------------------------------------------------------

/// Regenerates the entire static website from source files (markdown and fsx).
Target "Generate" (fun _ ->
    buildSite Production true
)

Target "Preview" (fun _ ->
    buildSite Preview true

    let server : ref<option<HttpServer>> = ref None
    
    let stop () = server.Value |> Option.iter (fun v -> v.Stop())
    
    let run() =
        let url = "http://localhost:8080/" 
        stop ()
        server := Some(HttpServer.Start(url, output, Replacements = [root, url]))
        printfn "Starting web server at %s" url
        System.Diagnostics.Process.Start(url) |> ignore
        
    run ()

    traceImportant "Press Ctrl+C to stop!"
    
    while true do ()
)

Target "New" (fun _ ->
    let post, fsx, page, beer =
        getBuildParam "post",
        getBuildParam "fsx",
        getBuildParam "page",
        getBuildParam "beer"

    match page, post, fsx, beer with
    | "", "", "", "" -> traceError "Please specify either a new 'page', 'post', 'beer', or 'fsx'."
    | _, "", "", ""  -> PostHelpers.CreateMarkdownPage source page
    | "", _, "", ""  -> PostHelpers.CreateMarkdownPost blog post "blogpost"
    | "", "", _, ""  -> PostHelpers.CreateFsxPost blog fsx
    | "", "", "", _  -> PostHelpers.CreateMarkdownPost beers beer "beerpost"
    | _, _, _, _    -> traceError "Please specify only one argument, 'post' or 'fsx'."
)

Target "Clean" (fun _ ->
    CleanDirs [output]
)

Target "Deploy" DoNothing

Target "Commit" DoNothing

Target "DoNothing" DoNothing

Target "GitClone" (fun _ ->
    if(FileSystemHelper.directoryExists(deploy ++ ".git")) then
        ()
    else
        Repository.cloneSingleBranch __SOURCE_DIRECTORY__ gitLocation.AbsoluteUri gitbranch deploy
)

Target "GitPublish" (fun _ ->
    CopyRecursive output deploy true |> ignore
    CommandHelper.runSimpleGitCommand deploy "add ." |> printfn "%s"
    let cmd = sprintf """commit -a -m "Update generated web site (%s)" """ (DateTime.Now.ToString("dd MMMM yyyy"))
    CommandHelper.runSimpleGitCommand deploy cmd |> printfn "%s"
    Branches.push deploy
)

Target "Install" (fun _ ->
    let theme = getBuildParam "theme"

    match theme with
    | "" -> traceError "Please specify theme"
    | _ ->
           CleanDir content
           CopyDir content (themes ++ theme) (fun file -> not(file.StartsWith(themes ++ theme ++ "source"))) |> ignore
           CopyRecursive (themes ++ theme ++ "source") source true |> ignore
)

"Clean" =?>
("Install", hasBuildParam "theme") ==>
"Generate"

"Clean" =?>
("Install", hasBuildParam "theme") ==>
"Preview"

"Generate" ==> "GitClone" ==> "GitPublish"

// --------------------------------------------------------------------------------------
// Run a specified target.
// --------------------------------------------------------------------------------------
RunTargetOrDefault "Preview"
