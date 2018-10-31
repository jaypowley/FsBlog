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
open TagTypes

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
      TaggedPosts : BlogTag[]
      TaggedBeers : BeerTag[]
      Beers : BeerHeader[] 
      GenerateAll : bool
      Root : string 
      Title : string }

  let urlFriendly (s:string) = s.Trim().Replace("#", "sharp").Replace(" ", "-").Replace(".", "dot").ToLower()
  let mapToBlogTagType (tag:string) (posts:seq<_>) = { Name = tag; Posts = posts |> Array.ofSeq } 
  let mapToBeerTagType (tag:string) (beers:seq<_>) = { Name = tag; Beers = beers |> Array.ofSeq } 

  /// Walks over all blog post files and loads model (caches abstracts along the way)
  let LoadModel(tagRenames, transformer, (root:string), blog, beer, title) = 
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
      TaggedPosts =
        query { for p in posts do
                for t in p.Tags do
                select t into t
                distinct
                let posts = posts |> Seq.filter (fun p -> p.Tags |> Seq.exists ((=) t))
                select (t, posts) }
        |> Seq.map (fun (tag, posts) -> mapToBlogTagType tag posts)
        |> Array.ofSeq
      TaggedBeers =
        query { for p in beers do
                for t in p.Tags do
                select t into t
                distinct
                let beers = beers |> Seq.filter (fun p -> p.Tags |> Seq.exists ((=) t))
                select (t, beers) }
        |> Seq.map (fun (tag, beers) -> mapToBeerTagType tag beers)
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

  let GenerateTagListing layouts template model output =
    let model = { model with GenerateAll = true }
    let razor = FsBlogLib.Razor(layouts, Model = model)
    let filterBeers (taggedBeers:BeerTag[]) tagName = 
        taggedBeers
        |> Seq.ofArray
        |> Seq.filter (fun x -> x.Name = tagName)
        |> Seq.toArray
    let filterPosts (taggedPosts:BlogTag[]) tagName =
        taggedPosts
        |> Seq.ofArray
        |> Seq.filter (fun x -> x.Name = tagName)
        |> Seq.toArray
    
    //beer tags
    let beerTarget = output ++ "beer" ++ "tags" ++ "index.html"
    EnsureDirectory(Path.GetDirectoryName(beerTarget))
    if not (File.Exists(beerTarget)) then
        TransformFile template true razor None "../BlogContent/beer/tags/index.cshtml" beerTarget

    for beer in model.TaggedBeers do
        let tagName = urlFriendly beer.Name
        let beerTagTarget = output ++ "beer" ++ "tags" ++ tagName ++ "index.html"
        let beermodel = { model with GenerateAll = true; TaggedBeers = filterBeers model.TaggedBeers beer.Name }
        let beerrazor = FsBlogLib.Razor(layouts, Model = beermodel)
        printfn "Generating folder: %s" tagName
        EnsureDirectory(Path.GetDirectoryName(beerTagTarget))
        if not (File.Exists(beerTagTarget)) then
            TransformFile template true beerrazor None "../BlogContent/beer/tags/tag/tagindex.cshtml" beerTagTarget

    //blog tags
    let blogTarget = output ++ "blog" ++ "tags" ++ "index.html"
    EnsureDirectory(Path.GetDirectoryName(blogTarget))
    if not (File.Exists(blogTarget)) then
        TransformFile template true razor None "../BlogContent/blog/tags/index.cshtml" blogTarget

    for blog in model.TaggedPosts do
        let tagName = urlFriendly blog.Name
        let blogTagTarget = output ++ "blog" ++ "tags" ++ tagName ++ "index.html"
        let blogmodel = { model with GenerateAll = true; TaggedPosts = filterPosts model.TaggedPosts blog.Name }
        let blograzor = FsBlogLib.Razor(layouts, Model = blogmodel)
        printfn "Generating folder: %s" tagName
        EnsureDirectory(Path.GetDirectoryName(blogTagTarget))
        if not (File.Exists(blogTagTarget)) then
            TransformFile template true blograzor None "../BlogContent/blog/tags/tag/tagindex.cshtml" blogTagTarget

  let GenerateSitemap root output target = 
    printfn "Generating %s" target
    let sitemapXmlNamespace = "http://www.sitemaps.org/schemas/sitemap/0.9"
 
    let xname str = XName.Get(str, sitemapXmlNamespace)
 
    let sourceFiles path =
        let fileList =
            [ for file in Directory.EnumerateFiles(path, "*.html", SearchOption.AllDirectories) do
                let index = path.Length
                let rtn = file.Substring(index)
                yield rtn ]
        fileList.Tail // excluding first item = root index.html
 
    let urls = [ for file in (sourceFiles output) do
                    let uri = new Uri(root, file)
                    yield uri.AbsoluteUri ]

    let urlItems = List.append [root.AbsoluteUri] urls 
 
    let items =
        [ for url in urlItems ->
               XElement
                ( xname "url",
                  XElement( xname "loc", url ),
                  XElement( xname "lastmod", DateTime.Now) )
        ]
 
    let urlset = XElement ( xname "urlset", List.toArray items )
 
    let doc = new XDocument(new XDeclaration("1.0", "UTF-8", "true"))
    doc.Add(urlset)
    EnsureDirectory(Path.GetDirectoryName(target))
    doc.Save(target)