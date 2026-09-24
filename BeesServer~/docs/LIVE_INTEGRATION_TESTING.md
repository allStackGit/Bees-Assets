# BeesServer test environment

The Bees test server has one persistent Linux configuration shared by qualification and the game-facing test server.

## Normal workflow

After pulling changes and installing dependencies:

```bash
npm install
npm test
```

`npm test` automatically:

1. loads the checked-in `testServerConfig.js` configuration;
2. requires port 7146 to be free;
3. migrates `bees_test`;
4. syntax-checks the server entrypoints;
5. starts a temporary real BeesServer on port 7146 using `bees_test`;
6. runs the complete Node test suite, including live schema and WebSocket/MySQL integration tests;
7. shuts the temporary server down;
8. exits non-zero if any step fails.

No environment-variable setup is required.

Once `npm test` succeeds, start the same test server environment for Unity:

```bash
npm run start:test
```

Leave that process running while the game is connected.

## Persistent test configuration

`testServerConfig.js` contains the disposable test-server configuration, including the MySQL credentials, database name, port, WebSocket URL, and reserved integration-test user ID. The test database contains no critical data, so these credentials are intentionally checked in rather than requiring per-shell environment setup.

Current test environment:

- database host: `127.0.0.1`
- database: `bees_test`
- server port: `7146`
- integration-test user: `900000001`

Test mode is also enforced inside `Database`: a test-mode server selects `bees_test` regardless of a requested normal database name.

## Qualification coverage

The live qualification exercises the real WebSocket -> BeesServer -> MySQL -> response path. It verifies application schema, current settings, learning-table key/counter widths, hot lookup indexes, user-data persistence, strategy retrieval, transactional outcome persistence, and reconnect ownership.

The exact selected strategy is intentionally not asserted because policy selection is stochastic.

## Port ownership

Do not leave `npm run start:test` running while invoking `npm test`. Qualification deliberately refuses to begin if port 7146 is already occupied so it cannot accidentally test an old or unrelated server process.

## Production

Production remains separate. `npm start` is the non-test server path, and production migrations retain their explicit production safety gate. Do not use the production database for automated qualification.
