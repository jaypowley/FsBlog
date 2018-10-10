namespace FsBlogLib
open System.IO

// --------------------------------------------------------------------------------------
// Parsing blog posts etc.
// --------------------------------------------------------------------------------------
module PostHelpers = 

  open FileHelpers
  open System.Text.RegularExpressions

  /// Get all *.cshtml, *.html, *.md and *.fsx files in the blog directory
  let GetPostFiles blog = seq {
    let exts = set [ ".md"; ".fsx"; ".cshtml"; ".html" ]
    let dirs = Array.append [| blog |] (Directory.GetDirectories(blog))
    for dir in dirs do
      if Path.GetFileNameWithoutExtension(dir) <> "abstracts" then
        for file in Directory.GetFiles(dir) do
          if exts |> Set.contains (Path.GetExtension(file).ToLower()) then
            if Path.GetFileNameWithoutExtension(file) <> "index" then
              yield file }
  
  let scriptHeaderRegex = 
    Regex("^\(\*\@(?<header>[^\*]*)\*\)(?<content>.*)$", RegexOptions.Singleline)
  let razorHeaderRegex = 
    Regex("^\@{(?<header>[^}]*)}(?<content>.*)$", RegexOptions.Singleline)
  let mdAbstractRegex = 
    Regex("(?<abstract>.*)<!--\s?more\s?-->", RegexOptions.Singleline)
  let fsxAbstractRegex = 
    Regex("(?<abstract>.*)\(\*\*\* more \*\*\*\)", RegexOptions.Singleline)

  /// An FSX file must start with a header (*@ ... *) which is removed 
  /// before Literate processing (and then added back as @{ ... }
  let RemoveScriptHeader ext file = 
    let content = File.ReadAllText(file)
    let reg = (match ext with | ".fsx" -> scriptHeaderRegex | _ -> razorHeaderRegex).Match(content)
    if not reg.Success then 
      failwithf "The following F# script or Markdown file is missing a header:\n%s" file 
    let header = reg.Groups.["header"].Value
    let body = reg.Groups.["content"].Value
    "@{" + header + "}\n", body

  /// An FSX file uses (*** more ***) to mark the end of what will be used as
  /// a post abstract - we must remove this before running the literate processing.
  let RemoveScriptAbstractMarker content = 
    (content:string).Replace("(*** more ***)", "")

  /// Return the header block of any blog post file
  let GetPostHeaderAndAbstract transformer prefix file =
    let ext = Path.GetExtension(file).ToLower()
    let headerRegex, abstractRegex =
      match ext with
      | ".fsx" -> scriptHeaderRegex, fsxAbstractRegex
      | ".md" | ".html" | ".cshtml" -> razorHeaderRegex, mdAbstractRegex
      | _ -> failwith "File format not supported!"
    let headerMatches = headerRegex.Match(File.ReadAllText(file))
    if not headerMatches.Success then 
      failwithf "The following source file is missing a header:\n%s" file 

    let header = headerMatches.Groups.["header"].Value
    let content = headerMatches.Groups.["content"].Value
    let abstractMatches = abstractRegex.Match(content)
    let rawAbstr = abstractMatches.Groups.["abstract"].Value 
    
    use fsx = DisposableFile.Create(file.Replace(ext, "_temp_" + ext))
    use html = DisposableFile.CreateTemp(".html")
    File.WriteAllText(fsx.FileName, rawAbstr)
    if ext = ".fsx" then
        FSharp.Literate.Literate.ProcessScriptFile(input=fsx.FileName, output=html.FileName) 
    else
        FSharp.Literate.Literate.ProcessMarkdown(input=fsx.FileName, output=html.FileName) 
    let abstr = File.ReadAllText(html.FileName)

    file, header, abstr

  /// Loads information about all posts
  let LoadPosts (tagRenames:System.Collections.Generic.IDictionary<string, string>) transformer posts parseHeader =
    let renameTag tag = 
      match tagRenames.TryGetValue(tag) with true, s -> s | _ -> tag //.ToLower()
    GetPostFiles posts 
    |> Seq.mapi (fun i v -> 
        GetPostHeaderAndAbstract transformer (sprintf "abs%d_" i) v 
        |> parseHeader renameTag posts )
    |> Array.ofSeq 
    |> Array.rev
 
  let markdownHeader layout (date:System.DateTime) title =
    sprintf """@{
    Layout = "%s";
    Title = "%s";
    AddedDate = "%s";
    Tags = "";
    Description = "";
}"""    layout title (date.ToString("yyyy-MM-ddThh:mm:ss"))

  let fsxHeader (date:System.DateTime) title = 
     sprintf """(*@
    Layout = "post";
    Title = "%s";
    AddedDate = "%s";
    Tags = "";
    Description = "";
    Image = "";
    PostAuthor = "";
*)"""   title (date.ToString("yyyy-MM-ddThh:mm:ss"))

  // Creates a new markdown page.
  let CreateMarkdownPage path title = 
    let now = System.DateTime.Now

    let prepend a b = sprintf "%s%s" a b

    let dir = 
        Regex.Matches(title, @"\w+")
        |> Seq.cast<Match>
        |> Seq.map (fun m -> m.ToString().ToLower())
        |> Seq.fold (fun s m -> 
            match s with
            | "" -> m
            | _  -> (sprintf "%s-%s" s m)
        ) ""
        |> prepend path
    
    let filename = dir + "/index.md"
    EnsureDirectory(dir)
    File.WriteAllText(filename, (markdownHeader "page" now title))

  /// News up a file at a specified path/filename with initial content generated
  /// from a header creation function.
  let CreateFile path createHeader ext title = 
    let append a b = sprintf "%s%s" b a
  
    // Perhaps parameterize this and bubble it up as a requirement?
    let now = System.DateTime.Now

    let dir = Path.Combine([|path;(sprintf "%i" now.Year)|])
    
    // Maybe use some kind of url formatting callback?
    let filename = 
        Regex.Matches(title, @"\w+")
        |> Seq.cast<Match>
        |> Seq.map (fun m -> m.ToString().ToLower())
        |> Seq.fold (fun s m -> (sprintf "%s-%s" s m)) (sprintf "%s/%s" dir (now.ToString("MM-dd")))
        |> append "."
        |> append ext

    EnsureDirectory(dir)
    File.WriteAllText(filename, (createHeader now title))

  /// Creates a new blank markdown post.
  let CreateMarkdownPost path title = 
    CreateFile path (markdownHeader "post") "md" title 

  /// Creates a new blank fsx post.
  let CreateFsxPost path title = 
    CreateFile path fsxHeader "fsx" title

  let fixformatting (str:string) = 
    str.Replace(System.Environment.NewLine, "").Replace("Summary ", "").Replace(" - YouTube","").Trim()

