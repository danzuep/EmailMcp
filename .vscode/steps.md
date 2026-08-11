# Setup Steps

Create the project:

```pwsh
# winget install --id Microsoft.DotNet.SDK.10 -e
# winget install --id Git.Git -e
# winget install --id github.cli -e
# cd C:\Source\danzuep
$orgName = "danzuep"
$projectName = "EmailMcp"
$gitUrl = "https://github.com/${orgName}/${projectName}.git"
$projectDescription = "MCP server for emails."
gh repo create --source=. --private --description $projectDescription # --confirm
mkdir $projectName
cd $projectName
git init
git add ".vscode/${projectName}.code-workspace"
git commit -m "Initial commit"
git branch -M main
git push -u origin main
mkdir src
cd src
dotnet new console -n $projectName
cd $projectName
dotnet add package "Microsoft.Extensions.Hosting"
dotnet add package "ModelContextProtocol"
dotnet add package "MailKitSimplified.Receiver"
dotnet add package "MailKitSimplified.Sender"                      
cd ..
dotnet new sln -n $projectName
dotnet sln add "${projectName}/${projectName}.csproj"
git add "${projectName}/${projectName}.csproj"
git commit -m "Project file"
git add "${projectName}.slnx"
git commit -m "Solution file"
cd ..
git add .vscode/steps.md
git commit -m "Setup steps file"
```
