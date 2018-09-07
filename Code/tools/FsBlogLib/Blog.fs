namespace FsBlogLib

open System
open System.IO
open BlogPosts
open BeerPosts
open FileHelpers
open PostHelpers
open System.Xml.Linq
open FSharp.Literate
open BlogTypes

// --------------------------------------------------------------------------------------
// Blog - the main blog functionality
// --------------------------------------------------------------------------------------

module Blog = 

  /// Represents the model that is passed to all pages
  type Model = 
    { AllPosts : Header[]
      Posts : BlogHeader[]
      MonthlyPosts : (int * string * seq<BlogHeader>)[]
      MonthlyBeerPosts : (int * string * seq<BeerHeader>)[]
      TaglyPosts : (string * string * seq<BlogHeader>)[]
      Beers : BeerHeader[] //seq<BeerHeader>
      GenerateAll : bool
      Root : string 
      Title : string }

  /// Walks over all blog post files and loads model (caches abstracts along the way)
  let LoadModel(tagRenames, transformer, (root:string), blog, beer, title) = 
    let urlFriendly (s:string) = s.Replace("#", "sharp").Replace(" ", "-").Replace(".", "dot")

    let p = LoadPosts tagRenames transformer blog ParseBlogHeader 
    let posts = p |> Array.choose (fun p -> match p with 
                                            | BlogHeader(p) -> Some(p)
                                            | BeerHeader(p) -> None)
    let b = LoadPosts tagRenames transformer beer ParseBeerHeader
    let beers = b |> Array.choose (fun p -> match p with 
                                                | BlogHeader(p) -> None
                                                | BeerHeader(p) -> Some(p))
    let allposts = 
        Array.concat [|p; b|]
        |> Array.sortBy(fun p -> match p with 
                                    | BlogHeader(p) -> p.AddedDate
                                    | BeerHeader(p) -> p.AddedDate)

    let uk = System.Globalization.CultureInfo.GetCultureInfo("en-GB")

    { AllPosts = allposts
      Posts = posts
      GenerateAll = false
      TaglyPosts =
        query { for p in posts do
                for t in p.Tags do
                select t into t
                distinct
                let posts = posts |> Seq.filter (fun p -> p.Tags |> Seq.exists ((=) t))
                let recent = posts |> Seq.filter (fun p -> p.AddedDate > (DateTime.Now.AddYears(-1))) |> Seq.length
                where (recent > 0)
                sortByDescending (recent * (Seq.length posts))
                select (t, urlFriendly t, posts) }
        |> Array.ofSeq
      MonthlyPosts =
        query { for p in posts do                
                groupBy (p.AddedDate.Year, p.AddedDate.Month) into g
                let year, month = g.Key
                sortByDescending (year, month)
                select (year, uk.DateTimeFormat.GetMonthName(month), g :> seq<_>) }
        |> Array.ofSeq
      MonthlyBeerPosts =
        query { for p in beers do                
                groupBy (p.AddedDate.Year, p.AddedDate.Month) into g
                let year, month = g.Key
                sortByDescending (year, month)
                select (year, uk.DateTimeFormat.GetMonthName(month), g :> seq<_>) }
        |> Array.ofSeq
      Root = root.Replace('\\', '/')
      Title = title
      Beers = beers
    }

  let TransformFile template hasHeader (razor:FsBlogLib.Razor) prefix current target =
    let html =
      match Path.GetExtension(current).ToLower() with
      | (".fsx" | ".md") as ext ->
          let header, content =
            if not hasHeader then "", File.ReadAllText(current)
            else RemoveScriptHeader ext current
          use fsx = DisposableFile.Create(current.Replace(ext, "_" + ext))
          use html = DisposableFile.CreateTemp(".html")
          File.WriteAllText(fsx.FileName, content |> RemoveScriptAbstractMarker)
          if ext = ".fsx" then
              Literate.ProcessScriptFile(fsx.FileName, template, html.FileName, ?prefix=prefix)
          else
            Literate.ProcessMarkdown(fsx.FileName, template, html.FileName, ?prefix=prefix)
          let processed = File.ReadAllText(html.FileName)
          File.WriteAllText(html.FileName, header + processed)
          EnsureDirectory(Path.GetDirectoryName(target))
          razor.ProcessFile(html.FileName)
      | ".html" | ".cshtml" ->
          EnsureDirectory(Path.GetDirectoryName(target))
          razor.ProcessFile(current)
      | _ -> failwith "Not supported file!"
    File.WriteAllText(target, html)

  let TransformAsTemp (template, source:string) razor prefix current =
    let cached = (Path.GetDirectoryName(current) ++ "cached" ++ Path.GetFileName(current))
    if File.Exists(cached) &&
      (File.GetLastWriteTime(cached) > File.GetLastWriteTime(current)) then
      File.ReadAllText(cached)
    else
      printfn "Processing abstract: %s" (current.Substring(source.Length + 1))
      EnsureDirectory(Path.GetDirectoryName(current) ++ "cached")
      TransformFile template false razor (Some prefix) current cached
      File.ReadAllText(cached)

  let GenerateRss root title description model take target =
    let count = Seq.length model.AllPosts
    let (!) name = XName.Get(name)
    let items =
      [| for item in model.AllPosts |> Seq.take (if count < take then count else take) -> 
            match item with 
                | BlogHeader(p) -> 
                    XElement
                        ( !"item",
                          XElement(!"title", p.Title),
                          XElement(!"guid", root + "blog/" + p.Url),
                          XElement(!"link", root + "blog/" + p.Url + "/index.html"),
                          XElement(!"pubDate", p.AddedDate.ToUniversalTime().ToString("r")),
                          XElement(!"description", p.Abstract)
                        )
                | BeerHeader(p) -> 
                    XElement
                        ( !"item",
                          XElement(!"title", p.Title),
                          XElement(!"guid", root + "beer/" + p.Url),
                          XElement(!"link", root + "beer/" + p.Url + "/index.html"),
                          XElement(!"pubDate", p.AddedDate.ToUniversalTime().ToString("r"))
                        )
      |]
    let channel = 
      XElement
        ( !"channel",
          XElement(!"title", (title:string)),
          XElement(!"link", (root:string)),
          XElement(!"description", (description:string)),
          items )
    let doc = XDocument(XElement(!"rss", XAttribute(!"version", "2.0"), channel))
    EnsureDirectory(Path.GetDirectoryName(target))
    File.WriteAllText(target, doc.ToString())

  let GeneratePostListing layouts template blogIndex model posts urlFunc needsUpdate infoFunc getPosts =
    for item in posts do
      let model = { model with GenerateAll = true; Posts = Array.ofSeq (getPosts item) }
      let razor = FsBlogLib.Razor(layouts, Model = model)
      let target = urlFunc item
      EnsureDirectory(Path.GetDirectoryName(target))
      if not (File.Exists(target)) || needsUpdate item then
        printfn "Generating archive: %s" (infoFunc item)
        TransformFile template true razor None blogIndex target

  let GenerateBeerListing layouts template blogIndex model posts urlFunc needsUpdate infoFunc getPosts =
        for item in posts do
          let model = { model with GenerateAll = true; Beers = Array.ofSeq (getPosts item) }
          let razor = FsBlogLib.Razor(layouts, Model = model)
          let target = urlFunc item
          EnsureDirectory(Path.GetDirectoryName(target))
          if not (File.Exists(target)) || needsUpdate item then
            printfn "Generating archive: %s" (infoFunc item)
            TransformFile template true razor None blogIndex target