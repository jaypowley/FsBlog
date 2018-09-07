namespace FsBlogLib

module BlogTypes = 

  /// Type that stores information about blog posts
  type BlogHeader = 
    { Title : string
      Abstract : string
      Description : string
      AddedDate : System.DateTime
      Url : string
      Tags : seq<string>      
      }
      
  /// Type that stores information about beer posts
  type BeerHeader = 
    { Title : string
      Description : string
      AddedDate : System.DateTime
      Url : string      
      Tags : seq<string> 
    }

  type Header = 
        | BlogHeader of BlogHeader
        | BeerHeader of BeerHeader
