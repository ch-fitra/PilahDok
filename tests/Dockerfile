FROM mcr.microsoft.com/dotnet/sdk:10.0.302-noble@sha256:72dd743782f2ae7e5476fd64f6a460045e3998dc862218b80e6944cba79a01b0 AS test-backend
WORKDIR /src
COPY . .
RUN dotnet restore tests/PilahDok.Tests/PilahDok.Tests.csproj
RUN dotnet format tests/PilahDok.Tests/PilahDok.Tests.csproj --verify-no-changes --no-restore
RUN dotnet test tests/PilahDok.Tests/PilahDok.Tests.csproj -c Release --no-restore --logger "console;verbosity=normal" --collect:"XPlat Code Coverage"
RUN dotnet list tests/PilahDok.Tests/PilahDok.Tests.csproj package --vulnerable --include-transitive | tee /tmp/vulnerability-report.txt \
    && ! grep -q "has the following vulnerable packages" /tmp/vulnerability-report.txt
CMD ["dotnet", "--info"]

FROM test-backend AS fixtures
RUN dotnet restore tools/PilahDok.FixtureGenerator/PilahDok.FixtureGenerator.csproj --locked-mode \
    && dotnet run --project tools/PilahDok.FixtureGenerator/PilahDok.FixtureGenerator.csproj -c Release --no-restore -- /fixtures/sample-structural.docx \
    && dotnet run --project tools/PilahDok.FixtureGenerator/PilahDok.FixtureGenerator.csproj -c Release --no-restore -- /fixtures/sample-no-headings.docx --no-headings

FROM node:24.15.0-alpine@sha256:d1b3b4da11eefd5941e7f0b9cf17783fc99d9c6fc34884a665f40a06dbdfc94f AS test-web
WORKDIR /src/apps/web
COPY apps/web/package.json apps/web/package-lock.json ./
RUN npm ci --ignore-scripts
COPY apps/web/ .
RUN npm run format:check
RUN npm run lint
RUN npm test
RUN npm run build
RUN npm audit --audit-level=high
CMD ["npm", "test"]

FROM mcr.microsoft.com/playwright:v1.62.1-noble@sha256:dcc5531e97840b9b5e794f2814476b21571c5124a3fca2267d73041f56e7580e AS e2e
WORKDIR /workspace/apps/web
COPY apps/web/package.json apps/web/package-lock.json ./
RUN npm ci --ignore-scripts
COPY apps/web/ ./
COPY --from=fixtures /fixtures/ /workspace/tests/fixtures/
ENV E2E_BASE_URL=http://web:3000
CMD ["npm", "run", "test:e2e"]
