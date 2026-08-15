# Application Tracker

A personal .NET 8 Blazor application for keeping every job application, saved job description, exact resume version, status, recruiter contact, and interview date together.

## Features

- Dashboard with applications submitted today, in the last seven days, and by date
- Add, view, edit, delete, search, and filter applications
- Save the complete job description even after the online posting disappears
- Attach the exact PDF/DOC/DOCX resume used for each application (up to 10 MB)
- Track status, recruiter information, notes, and next-step dates
- Local SQLite database; no separate database server required

## Run in Visual Studio 2022

1. Install the **ASP.NET and web development** workload and make sure the .NET 8 SDK is available.
2. Open `ApplicationTracker.sln` in Visual Studio.
3. Wait for NuGet package restore to finish.
4. Set `ApplicationTracker` as the startup project if Visual Studio does not select it automatically.
5. Select the `https` launch profile and press **F5**.

The app creates `application-tracker.db` automatically the first time it starts.

## Privacy and backup

All information stays on the computer running the application. Back up the `application-tracker.db` file regularly because it contains your records and attached resumes.
