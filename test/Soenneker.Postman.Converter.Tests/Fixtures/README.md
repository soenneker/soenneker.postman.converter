# LinkedIn regression fixtures

`linkedin-campaign-management.postman.json` is the Campaign Management collection supplied for this converter review, retrieved on September 18, 2026 from:

https://www.postman.com/collections/17563548-fab92f95-bfc5-4949-a52b-e760cc56e195

It contains 72 requests, Postman v2.0 OAuth configuration, Rest.li compound identifiers, multiple use cases for the same method/path, unresolved environment variables, scripts, and no saved responses. Tests use this local snapshot and never call LinkedIn or Postman. The collection content belongs to its original publisher.

`linkedin-content-apis.postman.json` is a snapshot of the Content APIs collection from:

https://www.postman.com/collections/17563548-d647a145-ed20-464a-b414-fa596f6ed06e

It contains 55 requests, including `Get document content` (ID `f716998a-532a-4b4e-ab35-4abdd20dfd84`) with a method and empty headers but no URL. The regression checks the runner's file conversion entry point and verifies that all 54 other requests are preserved in operations while the incomplete item is preserved in a document extension.
