namespace FsBlogLib
 
open BlogTypes

module TagTypes = 

  /// Type that stores information about blog posts
  type BlogTag = 
    { Name : string
      Posts : BlogHeader[]
    }
      
  /// Type that stores information about beer posts
  type BeerTag = 
    { Name : string
      Beers : BeerHeader[]
    }