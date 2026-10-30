# ChessRealms web app

Standard Angular CLI application with standalone components, routing, CSS, and Vitest.

From this directory:

```shell
npm ci
npm start
npm run build
npm test -- --watch=false
```

The standalone development server is available at http://localhost:4200.
To run the full platform, start the Aspire AppHost from the repository root;
it installs npm dependencies and starts this application as the `web` resource.
Open the endpoint shown in the Aspire dashboard.
