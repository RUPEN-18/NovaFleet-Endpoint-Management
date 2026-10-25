# Upload this source to GitHub

Extract the ZIP and work inside the folder that contains README.md and AutoShutdown.Nova.sln. Create an empty GitHub repository without adding another README, license or gitignore. Select visibility deliberately. Use a private repository while reviewing the recovered source.

From a terminal in the extracted folder:

```powershell
git init -b main
git add .
git diff --cached --check
git diff --cached --stat
```

Review the staged files and configuration before committing. No credentials, pairing files, private keys or runtime records belong in the commit. Then:

```powershell
git commit -m "Add recovered AutoShutdown Nova source"
```

Add your actual repository URL, authenticate with your own GitHub account, and push. Do not paste a GitHub token into the README or into chat. Use the normal Git authentication prompt.

```powershell
git remote add origin https://github.com/YOUR-USERNAME/YOUR-REPOSITORY.git
git push -u origin main
```

The URL above is a placeholder. This archive does not create a remote repository or push anything. The Windows build workflow is included but has not run yet. Inspect its result after upload before treating this source as build verified.

GitHub instructions: https://docs.github.com/en/migrations/importing-source-code/using-the-command-line-to-import-source-code/adding-locally-hosted-code-to-github
