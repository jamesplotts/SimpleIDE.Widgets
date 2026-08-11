' Utilities/ThemeManager.vb - Theme management for SimpleIDE
Imports Gtk
Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Reflection
Imports System.Text.Json
Imports System.Text.Json.Serialization
Imports SimpleIDE.Models
Imports SimpleIDE.Managers
Imports SimpleIDE.Utilities

Namespace Managers

    Public Class ThemeManager

        ''' <summary>
        ''' Name of the pseudo-theme that mirrors the desktop/GTK theme's actual background,
        ''' text, and selection colors rather than a fixed hand-picked palette
        ''' </summary>
        ''' <remarks>
        ''' Not a real built-in theme entry from GetAllBuiltInThemes - registered directly into
        ''' pAvailableThemes (see RegisterSystemColorsPlaceholder/RefreshSystemColorsTheme)
        ''' and deliberately never added to pCustomThemes, so DeleteTheme's "only custom themes
        ''' can be deleted" check protects it the same way it protects real built-ins
        ''' </remarks>
        Public Const SystemColorsThemeName As String = "System Colors"

        ' Private fields
        Private pSettingsManager As SettingsManager
        Private pCurrentTheme As EditorTheme
        Private pAvailableThemes As Dictionary(Of String, EditorTheme)
        Private pCssProvider As CssProvider
        Private pCustomThemes As List(Of EditorTheme)

        ' Events
        Public Event ThemeChanged(vTheme As EditorTheme)
        Public Event ThemeApplied(vThemeName As String)
        Public Event ThemeListChanged()

        ' Constructor
        Public Sub New(vSettingsManager As SettingsManager)
            pSettingsManager = vSettingsManager
            pAvailableThemes = New Dictionary(Of String, EditorTheme)
            pCustomThemes = New List(Of EditorTheme)
            pCssProvider = New CssProvider()

            ' Initialize themes
            LoadBuiltInThemes()
            LoadCustomThemes()

            ' Register a cheap placeholder for "System Colors" immediately (just Default
            ' Dark/Light cloned based on the GTK dark-mode setting) so the theme exists and is
            ' selectable right away, including if it's the persisted CurrentTheme being applied
            ' by SetTheme below. Sampling real GTK widget colors requires a realized window,
            ' which doesn't exist yet this early in startup - see RefreshSystemColorsTheme's
            ' remarks. MainWindow calls RefreshSystemColorsTheme once the window is realized to
            ' replace this placeholder with the real sampled colors.
            RegisterSystemColorsPlaceholder()

            ' Load current theme from settings
            Dim lThemeName As String = pSettingsManager.GetSetting("CurrentTheme", "Default Dark")
            SetTheme(lThemeName)
        End Sub

        Public ReadOnly Property SettingsManager() As SettingsManager
            Get
                Return pSettingsManager
            End Get
        End Property
        
        ' Get current theme name
        Public Function GetCurrentTheme() As String
            Return If(pCurrentTheme?.Name, "Default Dark")
        End Function
        
        ' Get current theme object
        Public Function GetCurrentThemeObject() As EditorTheme
            Return pCurrentTheme
        End Function
        
        ' Get list of available theme names
        Public Function GetAvailableThemes() As List(Of String)
            Dim lThemes As New List(Of String)
            
            for each lThemeName in pAvailableThemes.Keys
                lThemes.Add(lThemeName)
            Next
            
            Return lThemes
        End Function
        
        ' Get theme by name
        Public Function GetTheme(vThemeName As String) As EditorTheme
            If pAvailableThemes.ContainsKey(vThemeName) Then
                Return pAvailableThemes(vThemeName)
            End If
            
            Return Nothing
        End Function

        ''' <summary>
        ''' Determines if a theme is a custom (user-created) theme
        ''' </summary>
        ''' <param name="vThemeName">Name of the theme to check</param>
        ''' <returns>True if the theme is custom, False if it's built-in</returns>
        Public Function IsCustomTheme(vThemeName As String) As Boolean
            Try
                ' Check if the theme exists in the custom themes list
                Return pCustomThemes.Any(Function(t) t.Name = vThemeName)
                
            Catch ex As Exception
                Console.WriteLine($"IsCustomTheme error: {ex.Message}")
                Return False
            End Try
        End Function
        
        ' Set current theme
        Public Sub SetTheme(vThemeName As String)
            Try
                ' Re-sample the live GTK/desktop colors on every switch TO System Colors, not
                ' just once at startup, so it reflects the current OS theme even if that theme
                ' changed since SimpleIDE was launched
                If vThemeName = SystemColorsThemeName Then
                    RefreshSystemColorsTheme()
                End If

                If Not pAvailableThemes.ContainsKey(vThemeName) Then
                    #If DEBUG Then
                    Console.WriteLine($"Theme '{vThemeName}' not found, using default")
                    #End If
                    vThemeName = "Default Dark"
                End If

                pCurrentTheme = pAvailableThemes(vThemeName)
                
                ' Save to settings
                pSettingsManager.SetSetting("CurrentTheme", vThemeName)
                
                ' Apply theme
                ApplyCurrentTheme()
                
                ' Raise events
                RaiseEvent ThemeChanged(pCurrentTheme)
                RaiseEvent ThemeApplied(vThemeName)
                
            Catch ex As Exception
                Console.WriteLine($"SetTheme error: {ex.Message}")
            End Try
        End Sub
        
        ' Apply theme by name
        Public Sub ApplyTheme(vThemeName As String)
            SetTheme(vThemeName)
        End Sub
        
        ' Apply current theme
        Public Sub ApplyCurrentTheme()
            Try
                ' CRITICAL: Ensure we're on the UI thread
                ' GTK CSS operations MUST happen on the main thread
                If Not IsOnMainThread() Then
                    ' Schedule on UI thread and return
                    Gtk.Application.Invoke(Sub()
                        ApplyCurrentTheme()
                    End Sub)
                    Return
                End If
                
                If pCurrentTheme Is Nothing Then Return
                
                ' Generate CSS from theme
                Dim lCss As String = GenerateThemeCss(pCurrentTheme)
                
                ' Remove ALL previous CSS providers to ensure clean slate
                RemoveAllThemeProviders()
                
                ' Create new CSS provider
                pCssProvider = New CssProvider()
                pCssProvider.LoadFromData(lCss)
                
                ' Apply with high priority to override default GTK theme
                StyleContext.AddProviderForScreen(
                    Gdk.Screen.Default,
                    pCssProvider,
                    CUInt(StyleProviderPriority.User)  ' Use USER priority (800) for highest precedence
                )
                
                #If DEBUG Then
                Console.WriteLine($"ThemeManager.ApplyCurrentTheme: Applied theme: {pCurrentTheme.Name}")
                #End If
                
                ' Force GTK to refresh all widgets
                ForceGlobalRefresh()
                
            Catch ex As Exception
                Console.WriteLine($"ApplyCurrentTheme error: {ex.Message}")
            End Try
        End Sub

        ''' <summary>
        ''' Checks if we're currently on the main GTK thread
        ''' </summary>
        Private Function IsOnMainThread() As Boolean
            Try
                ' In GTK#, we can check if we're on the main thread by trying to access
                ' a thread-local property. This is a simple heuristic.
                Return System.Threading.Thread.CurrentThread.ManagedThreadId = 1
            Catch
                ' If we can't determine, assume we're not on main thread for safety
                Return False
            End Try
        End Function
        
        ' Generate CSS from theme
        Private Function GenerateThemeCss(vTheme As EditorTheme) As String
            Try
                Dim lCss As New Text.StringBuilder()
                
                ' Global styles
                lCss.AppendLine("/* SimpleIDE Theme CSS */")
                lCss.AppendLine($"* {{")
                lCss.AppendLine($"    color: {vTheme.ForegroundColor};")
                lCss.AppendLine($"    background-color: {vTheme.BackgroundColor};")
                lCss.AppendLine($"}}")
                lCss.AppendLine()
                
                ' Window styles
                lCss.AppendLine($"window {{")
                lCss.AppendLine($"    background-color: {vTheme.BackgroundColor};")
                lCss.AppendLine($"}}")
                lCss.AppendLine()
                
                ' Editor styles - FIXED: Changed pt to px
                lCss.AppendLine($".Editor {{")
                lCss.AppendLine($"    font-family: {vTheme.FontFamily};")
                lCss.AppendLine($"    font-size: {vTheme.FontSize}px;")  ' Changed from pt to px
                lCss.AppendLine($"    color: {vTheme.ForegroundColor};")
                lCss.AppendLine($"    background-color: {vTheme.BackgroundColor};")
                lCss.AppendLine($"}}")
                lCss.AppendLine()
                
                ' TreeView styles
                lCss.AppendLine($"treeview {{")
                If vTheme.IsDarkTheme Then
                    lCss.AppendLine($"    background-color: #252526;")
                    lCss.AppendLine($"    color: #CCCCCC;")
                Else
                    lCss.AppendLine($"    background-color: #F5F5F5;")
                    lCss.AppendLine($"    color: #000000;")
                End If
                lCss.AppendLine($"}}")
                lCss.AppendLine()
                
                lCss.AppendLine($"treeview:selected {{")
                lCss.AppendLine($"    background-color: {vTheme.SelectionColor};")
                lCss.AppendLine($"    color: #FFFFFF;")
                lCss.AppendLine($"}}")
                lCss.AppendLine()
                
                ' Notebook (tab control) styles
                lCss.AppendLine($"Notebook {{")
                If vTheme.IsDarkTheme Then
                    lCss.AppendLine($"    background-color: #2D2D30;")
                    lCss.AppendLine($"    border-color: #3E3E42;")
                Else
                    lCss.AppendLine($"    background-color: #F3F3F3;")
                    lCss.AppendLine($"    border-color: #CCCEDB;")
                End If
                lCss.AppendLine($"}}")
                lCss.AppendLine()
                
                ' Button styles
                lCss.AppendLine($"button {{")
                If vTheme.IsDarkTheme Then
                    lCss.AppendLine($"    background-color: #3E3E42;")
                    lCss.AppendLine($"    border-color: #555555;")
                Else
                    lCss.AppendLine($"    background-color: #FDFDFE;")
                    lCss.AppendLine($"    border-color: #C8C8C8;")
                End If
                lCss.AppendLine($"}}")
                lCss.AppendLine()
                
                lCss.AppendLine($"button:hover {{")
                If vTheme.IsDarkTheme Then
                    lCss.AppendLine($"    background-color: #4B4B4D;")
                Else
                    lCss.AppendLine($"    background-color: #F0F0F0;")
                End If
                lCss.AppendLine($"}}")
                lCss.AppendLine()
                
                ' Menu styles - use the theme's own BackgroundColor/ForegroundColor rather
                ' than a fixed VS-Code-gray fallback, so the menu bar matches the rest of
                ' the app instead of standing out as an obviously different shade on any
                ' theme whose background isn't close to that specific gray (e.g. Solarized
                ' Dark's dark teal #002B36 vs. the old hardcoded #2D2D30)
                lCss.AppendLine($"menu, menubar, menuitem {{")
                lCss.AppendLine($"    background-color: {vTheme.BackgroundColor};")
                lCss.AppendLine($"    color: {vTheme.ForegroundColor};")
                lCss.AppendLine($"}}")
                lCss.AppendLine()
                
                lCss.AppendLine($"menuitem:hover {{")
                lCss.AppendLine($"    background-color: {vTheme.SelectionColor};")
                lCss.AppendLine($"}}")
                lCss.AppendLine()
                
                ' Statusbar styles
                lCss.AppendLine($"statusbar {{")
                If vTheme.IsDarkTheme Then
                    lCss.AppendLine($"    background-color: #007ACC;")
                    lCss.AppendLine($"    color: #FFFFFF;")
                Else
                    lCss.AppendLine($"    background-color: #007ACC;")
                    lCss.AppendLine($"    color: #FFFFFF;")
                End If
                lCss.AppendLine($"}}")
                lCss.AppendLine()
                
                ' Paned separator styles
                lCss.AppendLine($"paned > separator {{")
                If vTheme.IsDarkTheme Then
                    lCss.AppendLine($"    background-color: #3E3E42;")
                    lCss.AppendLine($"    background-image: none;")
                Else
                    lCss.AppendLine($"    background-color: #CCCEDB;")
                    lCss.AppendLine($"    background-image: none;")
                End If
                lCss.AppendLine($"}}")
                
                Return lCss.ToString()
                
            Catch ex As Exception
                Console.WriteLine($"GenerateThemeCss error: {ex.Message}")
                Return ""
            End Try
        End Function
        
        ' Load built-in themes
        Private Sub LoadBuiltInThemes()
            Try
                ' Load predefined themes from EditorTheme
                Dim lBuiltInThemes As List(Of EditorTheme) = GetAllBuiltInThemes()
                
                for each lTheme in lBuiltInThemes
                    pAvailableThemes(lTheme.Name) = lTheme
                Next
                
                #If DEBUG Then
                Console.WriteLine($"loaded {pAvailableThemes.Count} built-in themes")
                #End If
                
            Catch ex As Exception
                Console.WriteLine($"LoadBuiltInThemes error: {ex.Message}")
                
                ' Ensure at least one theme exists
                If pAvailableThemes.Count = 0 Then
                    Dim lDefaultTheme As New EditorTheme("Default Dark")
                    pAvailableThemes(lDefaultTheme.Name) = lDefaultTheme
                End If
            End Try
        End Sub

        ' ===== System Colors (follows the desktop/GTK theme) =====

        ''' <summary>
        ''' Registers a cheap, immediately-available "System Colors" entry (just Default Dark
        ''' or Light cloned, based on the GTK dark-mode setting) so the theme exists and is
        ''' selectable from construction onward
        ''' </summary>
        Private Sub RegisterSystemColorsPlaceholder()
            Try
                Dim lIsDark As Boolean = Gtk.Settings.Default IsNot Nothing AndAlso Gtk.Settings.Default.ApplicationPreferDarkTheme
                Dim lBaseName As String = If(lIsDark, "Default Dark", "Light")
                Dim lBase As EditorTheme = If(pAvailableThemes.ContainsKey(lBaseName), pAvailableThemes(lBaseName), New EditorTheme("Default Dark"))

                Dim lPlaceholder As EditorTheme = lBase.Clone()
                lPlaceholder.Name = SystemColorsThemeName
                lPlaceholder.Description = "Follows the desktop/GTK theme's actual background, text, and selection colors"
                pAvailableThemes(SystemColorsThemeName) = lPlaceholder

            Catch ex As Exception
                Console.WriteLine($"RegisterSystemColorsPlaceholder error: {ex.Message}")
            End Try
        End Sub

        ''' <summary>
        ''' Re-samples "System Colors" from the live GTK/desktop theme and, if it's the
        ''' currently active theme, re-applies it immediately
        ''' </summary>
        ''' <remarks>
        ''' Called by SetTheme every time the user switches TO System Colors (so it reflects
        ''' whatever the OS theme currently is, not just whatever it was at app startup) and
        ''' once by MainWindow after the main window is realized (replacing
        ''' RegisterSystemColorsPlaceholder's construction-time placeholder with real sampled
        ''' colors, since sampling requires a realized widget - see BuildSystemColorsTheme).
        ''' There is no live push-based tracking of OS theme changes while SimpleIDE is
        ''' running - GTK3 has no reliable cross-desktop-environment signal for that - so a
        ''' theme switched at the OS level mid-session won't be picked up until the user
        ''' reselects System Colors (or restarts SimpleIDE).
        ''' </remarks>
        Public Sub RefreshSystemColorsTheme()
            Try
                Dim lTheme As EditorTheme = BuildSystemColorsTheme()
                If lTheme Is Nothing Then Return

                pAvailableThemes(SystemColorsThemeName) = lTheme

                If pCurrentTheme IsNot Nothing AndAlso pCurrentTheme.Name = SystemColorsThemeName Then
                    pCurrentTheme = lTheme
                    ApplyCurrentTheme()
                    RaiseEvent ThemeChanged(pCurrentTheme)
                End If

            Catch ex As Exception
                Console.WriteLine($"RefreshSystemColorsTheme error: {ex.Message}")
            End Try
        End Sub

        ''' <summary>
        ''' Builds the "System Colors" EditorTheme by sampling a live, realized top-level
        ''' window's actual GtkStyleContext - the real background/text/selection colors the
        ''' active desktop GTK theme is currently rendering everything else with
        ''' </summary>
        ''' <remarks>
        ''' SimpleIDE's own theme CSS is applied screen-wide at StyleProviderPriority.User (the
        ''' highest priority - see ApplyCurrentTheme), which would make any StyleContext query
        ''' just read back SimpleIDE's OWN theme instead of the underlying desktop one. This
        ''' temporarily removes that CSS override, forces an immediate style revalidation
        ''' (ResetStyle/RefreshWidgetRecursive - the same technique ForceGlobalRefresh already
        ''' uses), samples the now-unmasked real colors, then restores whatever theme was
        ''' active via ApplyCurrentTheme. GtkStyleContext resolves its queried properties
        ''' synchronously on access (not on the next paint), so this never actually paints the
        ''' unmasked desktop colors to screen - no visible flash.
        ''' </remarks>
        ''' <returns>The sampled EditorTheme, or Nothing if no realized window is available yet
        ''' (e.g. called before the main window has been shown)</returns>
        Private Function BuildSystemColorsTheme() As EditorTheme
            Try
                Dim lWindow As Window = Window.ListToplevels().FirstOrDefault(Function(w) w.Visible)
                If lWindow Is Nothing Then
                    #If DEBUG Then
                    Console.WriteLine("BuildSystemColorsTheme: no realized window yet, skipping sample")
                    #End If
                    Return Nothing
                End If

                Dim lIsDark As Boolean = Gtk.Settings.Default IsNot Nothing AndAlso Gtk.Settings.Default.ApplicationPreferDarkTheme

                ' Unmask the real desktop theme just long enough to sample it
                RemoveAllThemeProviders()
                lWindow.ResetStyle()
                RefreshWidgetRecursive(lWindow)

                Dim lStyleContext As StyleContext = lWindow.StyleContext
                Dim lBackgroundHex As String = CssHelper.RgbaToHex(lStyleContext.GetBackgroundColor(StateFlags.Normal))
                Dim lForegroundHex As String = CssHelper.RgbaToHex(lStyleContext.GetColor(StateFlags.Normal))

                ' GetBackgroundColor(StateFlags.Selected) on a bare Window's own StyleContext
                ' just returns the same value as Normal - a plain Window has no ":selected" CSS
                ' rule of its own (that only applies to specific selectable widgets like a
                ' GtkEntry's text selection or a GtkTreeView row). LookupColor against the
                ' theme's standard named "theme_selected_bg_color" (defined by Adwaita and most
                ' other GTK3 themes) is the correct way to get the theme's actual accent/
                ' selection color regardless of widget type. Falls back to a lightened/darkened
                ' variant of the background if the active theme doesn't define that name.
                Dim lSelectedRgba As New Gdk.RGBA()
                Dim lSelectionHex As String
                If lStyleContext.LookupColor("theme_selected_bg_color", lSelectedRgba) Then
                    lSelectionHex = CssHelper.RgbaToHex(lSelectedRgba)
                Else
                    lSelectionHex = If(lIsDark, LightenHex(lBackgroundHex, 0.25), DarkenHex(lBackgroundHex, 0.1))
                End If

                ' Base the syntax-highlighting palette and status colors on whichever built-in
                ' theme matches light/dark - there's no OS setting for "keyword color" or
                ' "string color" to sample, so this is the honest, readable fallback rather
                ' than guessing colors that might not contrast against the sampled background
                Dim lPaletteBaseName As String = If(lIsDark, "Default Dark", "Light")
                Dim lPaletteBase As EditorTheme = If(pAvailableThemes.ContainsKey(lPaletteBaseName), pAvailableThemes(lPaletteBaseName), New EditorTheme("Default Dark"))

                Dim lTheme As New EditorTheme(SystemColorsThemeName)
                lTheme.Description = "Follows the desktop/GTK theme's actual background, text, and selection colors"
                lTheme.IsDarkTheme = lIsDark
                lTheme.BackgroundColor = lBackgroundHex
                lTheme.ForegroundColor = lForegroundHex
                lTheme.SelectionColor = lSelectionHex
                lTheme.CurrentLineColor = If(lIsDark, LightenHex(lBackgroundHex, 0.06), DarkenHex(lBackgroundHex, 0.04))
                lTheme.LineNumberColor = BlendHex(lForegroundHex, lBackgroundHex, 0.4)
                lTheme.LineNumberBackgroundColor = lBackgroundHex
                lTheme.CurrentLineNumberColor = lForegroundHex
                lTheme.CursorColor = lForegroundHex
                lTheme.EditorBackgroundColor = lBackgroundHex
                lTheme.TabInactiveColor = If(lIsDark, LightenHex(lBackgroundHex, 0.04), DarkenHex(lBackgroundHex, 0.03))
                lTheme.TabHoverColor = If(lIsDark, LightenHex(lBackgroundHex, 0.08), DarkenHex(lBackgroundHex, 0.06))
                lTheme.AccentColor = lSelectionHex
                lTheme.BevelLightColor = If(lIsDark, LightenHex(lBackgroundHex, 0.15), "#F0F0F0")
                lTheme.BevelDarkColor = If(lIsDark, "#000000", DarkenHex(lBackgroundHex, 0.2))
                lTheme.DisabledForegroundColor = "" ' auto-derived from Foreground/Background - see EditorTheme.DeriveDisabledForegroundColor
                lTheme.ErrorColor = lPaletteBase.ErrorColor
                lTheme.WarningColor = lPaletteBase.WarningColor
                lTheme.InfoColor = lPaletteBase.InfoColor
                lTheme.SuccessColor = lPaletteBase.SuccessColor
                for each lKvp in lPaletteBase.SyntaxColors
                    lTheme.SyntaxColors(lKvp.Key) = lKvp.Value
                Next

                ' Carry over whatever editor font is currently configured rather than resetting
                ' to EditorTheme's own SetDefaults font - switching to System Colors shouldn't
                ' silently change the user's chosen editor font
                If pCurrentTheme IsNot Nothing Then
                    lTheme.FontFamily = pCurrentTheme.FontFamily
                    lTheme.FontSize = pCurrentTheme.FontSize
                End If

                ' Restore whatever theme was actually active before returning
                ApplyCurrentTheme()

                #If DEBUG Then
                Console.WriteLine($"BuildSystemColorsTheme: sampled Background={lBackgroundHex} Foreground={lForegroundHex} Selection={lSelectionHex} IsDark={lIsDark}")
                #End If
                Return lTheme

            Catch ex As Exception
                Console.WriteLine($"BuildSystemColorsTheme error: {ex.Message}")
                ' Best-effort attempt to restore the previous theme even on failure
                Try
                    ApplyCurrentTheme()
                Catch
                End Try
                Return Nothing
            End Try
        End Function

        ''' <summary>
        ''' Lightens a hex color toward white by vAmount (0.0-1.0 per channel)
        ''' </summary>
        Private Shared Function LightenHex(vHexColor As String, vAmount As Double) As String
            Try
                Dim lColor As New Gdk.RGBA()
                If Not lColor.Parse(vHexColor) Then Return vHexColor

                Dim lR As Double = Math.Min(1.0, lColor.Red + vAmount)
                Dim lG As Double = Math.Min(1.0, lColor.Green + vAmount)
                Dim lB As Double = Math.Min(1.0, lColor.Blue + vAmount)

                Return $"#{CInt(lR * 255):X2}{CInt(lG * 255):X2}{CInt(lB * 255):X2}"
            Catch ex As Exception
                Console.WriteLine($"LightenHex error: {ex.Message}")
                Return vHexColor
            End Try
        End Function

        ''' <summary>
        ''' Darkens a hex color toward black by vAmount (0.0-1.0 per channel)
        ''' </summary>
        Private Shared Function DarkenHex(vHexColor As String, vAmount As Double) As String
            Try
                Dim lColor As New Gdk.RGBA()
                If Not lColor.Parse(vHexColor) Then Return vHexColor

                Dim lR As Double = Math.Max(0.0, lColor.Red - vAmount)
                Dim lG As Double = Math.Max(0.0, lColor.Green - vAmount)
                Dim lB As Double = Math.Max(0.0, lColor.Blue - vAmount)

                Return $"#{CInt(lR * 255):X2}{CInt(lG * 255):X2}{CInt(lB * 255):X2}"
            Catch ex As Exception
                Console.WriteLine($"DarkenHex error: {ex.Message}")
                Return vHexColor
            End Try
        End Function

        ''' <summary>
        ''' Blends two hex colors - vRatio of 0.0 returns vColor1, 1.0 returns vColor2
        ''' </summary>
        Private Shared Function BlendHex(vColor1 As String, vColor2 As String, vRatio As Double) As String
            Try
                Dim lC1 As New Gdk.RGBA()
                Dim lC2 As New Gdk.RGBA()
                If Not lC1.Parse(vColor1) OrElse Not lC2.Parse(vColor2) Then Return vColor1

                Dim lR As Double = lC1.Red + (lC2.Red - lC1.Red) * vRatio
                Dim lG As Double = lC1.Green + (lC2.Green - lC1.Green) * vRatio
                Dim lB As Double = lC1.Blue + (lC2.Blue - lC1.Blue) * vRatio

                Return $"#{CInt(lR * 255):X2}{CInt(lG * 255):X2}{CInt(lB * 255):X2}"
            Catch ex As Exception
                Console.WriteLine($"BlendHex error: {ex.Message}")
                Return vColor1
            End Try
        End Function


        ' Get all built-in themes including popular ones
        Private Function GetAllBuiltInThemes() As List(Of EditorTheme)
            Dim lThemes As New List(Of EditorTheme)
            
            ' Get base themes
            lThemes.AddRange(EditorTheme.GetBuiltInThemes())
            
            ' Add more popular themes
            
            ' Monokai theme
            Dim lMonokai As New EditorTheme("Monokai")
            lMonokai.Description = "Popular dark theme"
            lMonokai.IsDarkTheme = True
            lMonokai.BackgroundColor = "#272822"
            lMonokai.ForegroundColor = "#F8F8F2"
            lMonokai.SelectionColor = "#49483E"
            lMonokai.CurrentLineColor = "#3E3D32"
            lMonokai.LineNumberColor = "#90908A"
            lMonokai.LineNumberBackgroundColor = "#272822"
            lMonokai.CurrentLineNumberColor = "#F8F8F2"
            lMonokai.CursorColor = "#F8F8F0"
            lMonokai.BevelLightColor = "#74746E"
            lMonokai.BevelDarkColor = "#000000"
            lMonokai.DisabledForegroundColor = "#9A9A94"
            lMonokai.AccentColor = "#F92672" ' Monokai's signature hot pink/magenta
            lMonokai.ErrorColor = "#F92672"
            lMonokai.WarningColor = "#FD971F"
            lMonokai.InfoColor = "#75715E"
            lMonokai.SuccessColor = "#A6E22E"
            lMonokai.SyntaxColors(SyntaxColorSet.Tags.eKeyword) = "#F92672"
            lMonokai.SyntaxColors(SyntaxColorSet.Tags.eType) = "#66D9EF"
            lMonokai.SyntaxColors(SyntaxColorSet.Tags.eString) = "#E6DB74"
            lMonokai.SyntaxColors(SyntaxColorSet.Tags.eComment) = "#75715E"
            lMonokai.SyntaxColors(SyntaxColorSet.Tags.eNumber) = "#AE81FF"
            lMonokai.SyntaxColors(SyntaxColorSet.Tags.eIdentifier) = "#F8F8F2"
            lMonokai.SyntaxColors(SyntaxColorSet.Tags.eSelection) = lMonokai.BackgroundColor
            lThemes.Add(lMonokai)
            
            ' Solarized Dark theme
            Dim lSolarizedDark As New EditorTheme("Solarized Dark")
            lSolarizedDark.Description = "Precision colors for machines and people"
            lSolarizedDark.IsDarkTheme = True
            lSolarizedDark.BackgroundColor = "#002B36"
            lSolarizedDark.ForegroundColor = "#839496"
            lSolarizedDark.SelectionColor = "#073642"
            lSolarizedDark.CurrentLineColor = "#073642"
            lSolarizedDark.LineNumberColor = "#586E75"
            lSolarizedDark.LineNumberBackgroundColor = "#002B36"
            lSolarizedDark.CurrentLineNumberColor = "#93A1A1"
            lSolarizedDark.CursorColor = "#D33682"
            lSolarizedDark.BevelLightColor = "#4C7882"
            lSolarizedDark.BevelDarkColor = "#000000"
            lSolarizedDark.DisabledForegroundColor = "#48656B"
            lSolarizedDark.AccentColor = "#268BD2" ' Solarized's canonical "blue" accent
            lSolarizedDark.ErrorColor = "#DC322F"
            lSolarizedDark.WarningColor = "#CB4B16"
            lSolarizedDark.InfoColor = "#268BD2"
            lSolarizedDark.SuccessColor = "#859900"
            lSolarizedDark.SyntaxColors(SyntaxColorSet.Tags.eKeyword) = "#859900"
            lSolarizedDark.SyntaxColors(SyntaxColorSet.Tags.eType) = "#268BD2"
            lSolarizedDark.SyntaxColors(SyntaxColorSet.Tags.eString) = "#2AA198"
            lSolarizedDark.SyntaxColors(SyntaxColorSet.Tags.eComment) = "#586E75"
            lSolarizedDark.SyntaxColors(SyntaxColorSet.Tags.eNumber) = "#6C71C4"
            lSolarizedDark.SyntaxColors(SyntaxColorSet.Tags.eIdentifier) = "#839496"
            lSolarizedDark.SyntaxColors(SyntaxColorSet.Tags.eSelection) = lSolarizedDark.BackgroundColor
            lThemes.Add(lSolarizedDark)
            
            ' Solarized Light theme
            Dim lSolarizedLight As New EditorTheme("Solarized Light")
            lSolarizedLight.Description = "Precision colors for machines and people"
            lSolarizedLight.IsDarkTheme = False
            lSolarizedLight.BackgroundColor = "#FDF6E3"
            lSolarizedLight.ForegroundColor = "#657B83"
            lSolarizedLight.SelectionColor = "#EEE8D5"
            lSolarizedLight.CurrentLineColor = "#EEE8D5"
            lSolarizedLight.LineNumberColor = "#93A1A1"
            lSolarizedLight.LineNumberBackgroundColor = "#FDF6E3"
            lSolarizedLight.CurrentLineNumberColor = "#586E75"
            lSolarizedLight.CursorColor = "#D33682"
            lSolarizedLight.BevelLightColor = "#FFFFFF"
            lSolarizedLight.BevelDarkColor = "#B0AA96"
            lSolarizedLight.DisabledForegroundColor = "#A9B2AE"
            lSolarizedLight.AccentColor = "#268BD2" ' Same Solarized "blue" - designed to read on both base03/base3
            lSolarizedLight.ErrorColor = "#DC322F"
            lSolarizedLight.WarningColor = "#CB4B16"
            lSolarizedLight.InfoColor = "#268BD2"
            lSolarizedLight.SuccessColor = "#859900"
            lSolarizedLight.SyntaxColors(SyntaxColorSet.Tags.eKeyword) = "#859900"
            lSolarizedLight.SyntaxColors(SyntaxColorSet.Tags.eType) = "#268BD2"
            lSolarizedLight.SyntaxColors(SyntaxColorSet.Tags.eString) = "#2AA198"
            lSolarizedLight.SyntaxColors(SyntaxColorSet.Tags.eComment) = "#93A1A1"
            lSolarizedLight.SyntaxColors(SyntaxColorSet.Tags.eNumber) = "#6C71C4"
            lSolarizedLight.SyntaxColors(SyntaxColorSet.Tags.eIdentifier) = "#657B83"
            lSolarizedLight.SyntaxColors(SyntaxColorSet.Tags.eSelection) = lSolarizedLight.BackgroundColor
            lThemes.Add(lSolarizedLight)
            
            ' Dracula theme
            Dim lDracula As New EditorTheme("Dracula")
            lDracula.Description = "Dark theme for developers"
            lDracula.IsDarkTheme = True
            lDracula.BackgroundColor = "#282A36"
            lDracula.ForegroundColor = "#F8F8F2"
            lDracula.SelectionColor = "#44475A"
            lDracula.CurrentLineColor = "#44475A"
            lDracula.LineNumberColor = "#6272A4"
            lDracula.LineNumberBackgroundColor = "#282A36"
            lDracula.CurrentLineNumberColor = "#F8F8F2"
            lDracula.CursorColor = "#F8F8F2"
            lDracula.BevelLightColor = "#747682"
            lDracula.BevelDarkColor = "#000000"
            lDracula.DisabledForegroundColor = "#9A9B9D"
            lDracula.AccentColor = "#BD93F9" ' Dracula's signature purple
            lDracula.ErrorColor = "#FF5555"
            lDracula.WarningColor = "#FFB86C"
            lDracula.InfoColor = "#6272A4"
            lDracula.SuccessColor = "#50FA7B"
            lDracula.SyntaxColors(SyntaxColorSet.Tags.eKeyword) = "#FF79C6"
            lDracula.SyntaxColors(SyntaxColorSet.Tags.eType) = "#8BE9FD"
            lDracula.SyntaxColors(SyntaxColorSet.Tags.eString) = "#F1FA8C"
            lDracula.SyntaxColors(SyntaxColorSet.Tags.eComment) = "#6272A4"
            lDracula.SyntaxColors(SyntaxColorSet.Tags.eNumber) = "#BD93F9"
            lDracula.SyntaxColors(SyntaxColorSet.Tags.eIdentifier) = "#F8F8F2"
            lDracula.SyntaxColors(SyntaxColorSet.Tags.eSelection) = lDracula.BackgroundColor
            lThemes.Add(lDracula)
            
            ' GitHub Dark theme
            Dim lGitHubDark As New EditorTheme("GitHub Dark")
            lGitHubDark.Description = "GitHub's dark theme"
            lGitHubDark.IsDarkTheme = True
            lGitHubDark.BackgroundColor = "#0D1117"
            lGitHubDark.ForegroundColor = "#C9D1D9"
            lGitHubDark.SelectionColor = "#1F6FEB"
            lGitHubDark.CurrentLineColor = "#161B22"
            lGitHubDark.LineNumberColor = "#8B949E"
            lGitHubDark.LineNumberBackgroundColor = "#0D1117"
            lGitHubDark.CurrentLineNumberColor = "#C9D1D9"
            lGitHubDark.CursorColor = "#C9D1D9"
            lGitHubDark.BevelLightColor = "#5A5E64"
            lGitHubDark.BevelDarkColor = "#000000"
            lGitHubDark.DisabledForegroundColor = "#747B82"
            lGitHubDark.AccentColor = "#58A6FF" ' GitHub's actual dark-mode link/accent blue
            lGitHubDark.ErrorColor = "#DC322F"
            lGitHubDark.WarningColor = "#CB4B16"
            lGitHubDark.InfoColor = "#268BD2"
            lGitHubDark.SuccessColor = "#859900"
            lGitHubDark.SyntaxColors(SyntaxColorSet.Tags.eKeyword) = "#FF7B72"
            lGitHubDark.SyntaxColors(SyntaxColorSet.Tags.eType) = "#79C0FF"
            lGitHubDark.SyntaxColors(SyntaxColorSet.Tags.eString) = "#A5D6FF"
            lGitHubDark.SyntaxColors(SyntaxColorSet.Tags.eComment) = "#8B949E"
            lGitHubDark.SyntaxColors(SyntaxColorSet.Tags.eNumber) = "#79C0FF"
            lGitHubDark.SyntaxColors(SyntaxColorSet.Tags.eIdentifier) = "#C9D1D9"
            lGitHubDark.SyntaxColors(SyntaxColorSet.Tags.eSelection) = lGitHubDark.BackgroundColor
            lThemes.Add(lGitHubDark)
            
            ' One Dark theme
            Dim lOneDark As New EditorTheme("One Dark")
            lOneDark.Description = "Atom One Dark theme"
            lOneDark.IsDarkTheme = True
            lOneDark.BackgroundColor = "#282C34"
            lOneDark.ForegroundColor = "#ABB2BF"
            lOneDark.SelectionColor = "#3E4451"
            lOneDark.CurrentLineColor = "#2C323C"
            lOneDark.LineNumberColor = "#636D83"
            lOneDark.LineNumberBackgroundColor = "#282C34"
            lOneDark.CurrentLineNumberColor = "#ABB2BF"
            lOneDark.CursorColor = "#528BFF"
            lOneDark.BevelLightColor = "#747880"
            lOneDark.BevelDarkColor = "#000000"
            lOneDark.DisabledForegroundColor = "#707680"
            lOneDark.AccentColor = "#61AFEF" ' Atom One Dark's signature "function blue"
            lOneDark.ErrorColor = "#DC322F"
            lOneDark.WarningColor = "#CB4B16"
            lOneDark.InfoColor = "#268BD2"
            lOneDark.SuccessColor = "#859900"
            lOneDark.SyntaxColors(SyntaxColorSet.Tags.eKeyword) = "#C678DD"
            lOneDark.SyntaxColors(SyntaxColorSet.Tags.eType) = "#E06C75"
            lOneDark.SyntaxColors(SyntaxColorSet.Tags.eString) = "#98C379"
            lOneDark.SyntaxColors(SyntaxColorSet.Tags.eComment) = "#5C6370"
            lOneDark.SyntaxColors(SyntaxColorSet.Tags.eNumber) = "#D19A66"
            lOneDark.SyntaxColors(SyntaxColorSet.Tags.eIdentifier) = "#ABB2BF"
            lOneDark.SyntaxColors(SyntaxColorSet.Tags.eSelection) = lOneDark.BackgroundColor
            lThemes.Add(lOneDark)
            
            Return lThemes
        End Function
        
        ' Load custom themes from user directory
        Private Sub LoadCustomThemes()
            Try
                Dim lThemesDir As String = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "SimpleIDE", "Themes")
                
                If Directory.Exists(lThemesDir) Then
                    ' Load .json files
                    for each lThemeFile in Directory.GetFiles(lThemesDir, "*.json")
                        Try
                            Dim lTheme As EditorTheme = LoadThemeFromFile(lThemeFile)
                            If lTheme IsNot Nothing Then
                                pCustomThemes.Add(lTheme)
                                pAvailableThemes(lTheme.Name) = lTheme
                            End If
                        Catch ex As Exception
                            Console.WriteLine($"error loading theme file {lThemeFile}: {ex.Message}")
                        End Try
                    Next
                    
                    ' Also load .theme files for compatibility
                    for each lThemeFile in Directory.GetFiles(lThemesDir, "*.theme")
                        Try
                            Dim lTheme As EditorTheme = LoadThemeFromFile(lThemeFile)
                            If lTheme IsNot Nothing Then
                                pCustomThemes.Add(lTheme)
                                pAvailableThemes(lTheme.Name) = lTheme
                            End If
                        Catch ex As Exception
                            Console.WriteLine($"error loading theme file {lThemeFile}: {ex.Message}")
                        End Try
                    Next
                End If
                
                #If DEBUG Then
                Console.WriteLine($"loaded {pCustomThemes.Count} custom themes")
                #End If
                
            Catch ex As Exception
                Console.WriteLine($"LoadCustomThemes error: {ex.Message}")
            End Try
        End Sub
        
        ''' <summary>
        ''' Load theme from file
        ''' </summary>
        Private Function LoadThemeFromFile(vFilePath As String) As EditorTheme
            Try
                Dim lJson As String = File.ReadAllText(vFilePath)
                Dim lThemeData As ThemeData = JsonSerializer.Deserialize(Of ThemeData)(lJson)
                
                If lThemeData Is Nothing Then Return Nothing
                
                Dim lTheme As New EditorTheme(lThemeData.Name)
                lTheme.Description = lThemeData.Description
                lTheme.IsDarkTheme = lThemeData.IsDarkTheme
                lTheme.BackgroundColor = lThemeData.BackgroundColor
                lTheme.ForegroundColor = lThemeData.ForegroundColor
                lTheme.SelectionColor = lThemeData.SelectionColor
                lTheme.CurrentLineColor = lThemeData.CurrentLineColor
                lTheme.LineNumberColor = lThemeData.LineNumberColor
                lTheme.LineNumberBackgroundColor = lThemeData.LineNumberBackgroundColor
                lTheme.CurrentLineNumberColor = lThemeData.CurrentLineNumberColor
                lTheme.CursorColor = lThemeData.CursorColor
                
                ' Load status colors with defaults if missing
                lTheme.ErrorColor = If(lThemeData.ErrorColor, "#F48771")
                lTheme.WarningColor = If(lThemeData.WarningColor, "#CCA700")
                lTheme.InfoColor = If(lThemeData.InfoColor, "#75BEFF")
                lTheme.SuccessColor = If(lThemeData.SuccessColor, "#89D185")
                
                ' Load tab colors (NEW) - may not exist in older theme files
                lTheme.EditorBackgroundColor = If(lThemeData.EditorBackgroundColor, "")
                lTheme.TabInactiveColor = If(lThemeData.TabInactiveColor, "")
                lTheme.TabHoverColor = If(lThemeData.TabHoverColor, "")
                lTheme.AccentColor = If(lThemeData.AccentColor, "")
                
                
                ' Font settings
                lTheme.FontFamily = If(lThemeData.FontFamily, "Monospace")
                lTheme.FontSize = lThemeData.FontSize
'                 
                ' Syntax colors
                If lThemeData.SyntaxColors IsNot Nothing Then
                    for each lKvp in lThemeData.SyntaxColors
                        Try
                            Dim lTag As SyntaxColorSet.Tags = DirectCast([Enum].Parse(GetType(SyntaxColorSet.Tags), lKvp.Key), SyntaxColorSet.Tags)
                            lTheme.SyntaxColors(lTag) = lKvp.Value
                        Catch
                            ' Ignore invalid syntax color tags
                        End Try
                    Next
                End If
                
                Return lTheme
                
            Catch ex As Exception
                Console.WriteLine($"LoadThemeFromFile error: {ex.Message}")
                Return Nothing
            End Try
        End Function
        
        ''' <summary>
        ''' Save theme to file
        ''' </summary>
        Public Function SaveTheme(vTheme As EditorTheme, vFilePath As String) As Boolean
            Try
                Dim lThemeData As New ThemeData()
                lThemeData.Name = vTheme.Name
                lThemeData.Description = vTheme.Description
                lThemeData.IsDarkTheme = vTheme.IsDarkTheme
                lThemeData.BackgroundColor = vTheme.BackgroundColor
                lThemeData.ForegroundColor = vTheme.ForegroundColor
                lThemeData.SelectionColor = vTheme.SelectionColor
                lThemeData.CurrentLineColor = vTheme.CurrentLineColor
                lThemeData.LineNumberColor = vTheme.LineNumberColor
                lThemeData.LineNumberBackgroundColor = vTheme.LineNumberBackgroundColor
                lThemeData.CurrentLineNumberColor = vTheme.CurrentLineNumberColor
                lThemeData.CursorColor = vTheme.CursorColor
                
                ' Save status colors
                lThemeData.ErrorColor = vTheme.ErrorColor
                lThemeData.WarningColor = vTheme.WarningColor
                lThemeData.InfoColor = vTheme.InfoColor
                lThemeData.SuccessColor = vTheme.SuccessColor
                
                lThemeData.FontFamily = vTheme.FontFamily
                lThemeData.FontSize = vTheme.FontSize
                
                ' Save syntax colors
                lThemeData.SyntaxColors = New Dictionary(Of String, String)()
                for each kvp in vTheme.SyntaxColors
                    lThemeData.SyntaxColors(kvp.Key.ToString()) = kvp.Value
                Next
                
                Dim lOptions As New JsonSerializerOptions()
                lOptions.WriteIndented = True
                
                Dim lJson As String = JsonSerializer.Serialize(lThemeData, lOptions)
                File.WriteAllText(vFilePath, lJson)
                
                Return True
                
            Catch ex As Exception
                Console.WriteLine($"SaveTheme error: {ex.Message}")
                Return False
            End Try
        End Function

        
        ' Create custom theme
        Public Function CreateCustomTheme(vBasedOn As String, vNewName As String) As EditorTheme
            Try
                If Not pAvailableThemes.ContainsKey(vBasedOn) Then
                    Return Nothing
                End If
                
                Dim lBaseTheme As EditorTheme = pAvailableThemes(vBasedOn)
                Dim lNewTheme As EditorTheme = lBaseTheme.Clone()
                lNewTheme.Name = vNewName
                
                ' Add to available themes
                pAvailableThemes(vNewName) = lNewTheme
                pCustomThemes.Add(lNewTheme)
                
                ' Save to file
                Dim lThemesDir As String = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "SimpleIDE", "Themes")
                
                If Not Directory.Exists(lThemesDir) Then
                    Directory.CreateDirectory(lThemesDir)
                End If
                
                Dim lFilePath As String = System.IO.Path.Combine(lThemesDir, $"{vNewName}.json")
                SaveTheme(lNewTheme, lFilePath)
                
                RaiseEvent ThemeListChanged()
                Return lNewTheme
                
            Catch ex As Exception
                Console.WriteLine($"CreateCustomTheme error: {ex.Message}")
                Return Nothing
            End Try
        End Function
        
        ' Import theme from file
        Public Function ImportTheme(vFilePath As String) As EditorTheme
            Try
                Dim lTheme As EditorTheme = LoadThemeFromFile(vFilePath)
                If lTheme Is Nothing Then Return Nothing
                
                ' Check if theme name already exists
                If pAvailableThemes.ContainsKey(lTheme.Name) Then
                    ' Generate unique name
                    Dim lCounter As Integer = 1
                    Dim lNewName As String = lTheme.Name
                    While pAvailableThemes.ContainsKey(lNewName)
                        lNewName = $"{lTheme.Name} ({lCounter})"
                        lCounter += 1
                    End While
                    lTheme.Name = lNewName
                End If
                
                ' Add to themes
                pAvailableThemes(lTheme.Name) = lTheme
                pCustomThemes.Add(lTheme)
                
                ' Save to user themes directory
                Dim lThemesDir As String = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "SimpleIDE", "Themes")
                
                If Not Directory.Exists(lThemesDir) Then
                    Directory.CreateDirectory(lThemesDir)
                End If
                
                Dim lDestPath As String = System.IO.Path.Combine(lThemesDir, $"{lTheme.Name}.json")
                SaveTheme(lTheme, lDestPath)
                
                RaiseEvent ThemeListChanged()
                Return lTheme
                
            Catch ex As Exception
                Console.WriteLine($"ImportTheme error: {ex.Message}")
                Return Nothing
            End Try
        End Function
        
        ' Delete custom theme
        Public Function DeleteTheme(vThemeName As String) As Boolean
            Try
                ' Cannot delete built-in themes
                If Not pCustomThemes.any(Function(t) t.Name = vThemeName) Then
                    Return False
                End If
                
                ' Remove from collections
                If pAvailableThemes.ContainsKey(vThemeName) Then
                    pAvailableThemes.Remove(vThemeName)
                End If
                
                pCustomThemes.RemoveAll(Function(t) t.Name = vThemeName)
                
                ' Delete file
                Dim lThemesDir As String = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "SimpleIDE", "Themes")
                
                Dim lFilePath As String = System.IO.Path.Combine(lThemesDir, $"{vThemeName}.json")
                If File.Exists(lFilePath) Then
                    File.Delete(lFilePath)
                End If
                
                ' Also check for .theme file
                lFilePath = System.IO.Path.Combine(lThemesDir, $"{vThemeName}.theme")
                If File.Exists(lFilePath) Then
                    File.Delete(lFilePath)
                End If
                
                ' If deleted theme was current, switch to default
                If pCurrentTheme IsNot Nothing AndAlso pCurrentTheme.Name = vThemeName Then
                    SetTheme("Default Dark")
                End If
                
                RaiseEvent ThemeListChanged()
                Return True
                
            Catch ex As Exception
                Console.WriteLine($"DeleteTheme error: {ex.Message}")
                Return False
            End Try
        End Function
        
        ' Get theme by name (compatibility)
        Public Function GetThemeCss(vThemeName As String) As String
            Try
                Dim lTheme As EditorTheme = GetTheme(vThemeName)
                If lTheme IsNot Nothing Then
                    Return GenerateThemeCss(lTheme)
                End If
                
                Return ""
                
            Catch ex As Exception
                Console.WriteLine($"GetThemeCss error: {ex.Message}")
                Return ""
            End Try
        End Function
        
        ' Get the current editor theme for applying to editors
        Public Function GetEditorTheme() As EditorTheme
            Try
                ' Return current theme if available
                If pCurrentTheme IsNot Nothing Then
                    Return pCurrentTheme
                End If
                
                ' If no current theme, try to get the default
                If pAvailableThemes.ContainsKey("Default Dark") Then
                    Return pAvailableThemes("Default Dark")
                End If
                
                ' If no default, return the first available theme
                If pAvailableThemes.Count > 0 Then
                    Return pAvailableThemes.Values.First()
                End If
                
                ' Last resort: create a basic default theme
                #If DEBUG Then
                Console.WriteLine("GetEditorTheme: No themes available, creating default")
                #End If
                Dim lDefaultTheme As New EditorTheme("Default Dark")
                pAvailableThemes("Default Dark") = lDefaultTheme
                pCurrentTheme = lDefaultTheme
                Return lDefaultTheme
                
            Catch ex As Exception
                Console.WriteLine($"GetEditorTheme error: {ex.Message}")
                
                ' Return a basic theme on error
                Return New EditorTheme("Fallback Dark")
            End Try
        End Function

        ' New method to remove all theme providers
        Private Sub RemoveAllThemeProviders()
            Try
                ' Remove the current provider if it exists
                If pCssProvider IsNot Nothing Then
                    StyleContext.RemoveProviderForScreen(Gdk.Screen.Default, pCssProvider)
                    pCssProvider = Nothing
                End If
                
                ' Note: We can't remove other providers without references to them,
                ' but setting a new one with USER priority should override them
                
            Catch ex As Exception
                Console.WriteLine($"RemoveAllThemeProviders error: {ex.Message}")
            End Try
        End Sub
        
        ' New method to force global widget refresh
        Private Sub ForceGlobalRefresh()
            Try
                ' Get all toplevel windows and refresh them
                Dim lWindows As Window() = Window.ListToplevels()
                for each lWindow As Window in lWindows
                    If lWindow IsNot Nothing AndAlso lWindow.Visible Then
                        ' Reset style context to force re-evaluation
                        lWindow.ResetStyle()
                        
                        ' Queue redraw
                        lWindow.QueueDraw()
                        
                        ' Also refresh all children recursively
                        RefreshWidgetRecursive(lWindow)
                    End If
                Next
                
                ' Process pending events to ensure updates are applied
                While Application.EventsPending()
                    Application.RunIteration(False)
                End While
                
            Catch ex As Exception
                Console.WriteLine($"ForceGlobalRefresh error: {ex.Message}")
            End Try
        End Sub

        ' Recursive helper to refresh all widgets
        Private Sub RefreshWidgetRecursive(vWidget As Widget)
            Try
                If vWidget Is Nothing Then Return
                
                ' Reset the widget's style
                vWidget.ResetStyle()
                vWidget.QueueDraw()
                
                ' If it's a container, refresh all children
                Dim lContainer As Container = TryCast(vWidget, Container)
                If lContainer IsNot Nothing Then
                    for each lChild As Widget in lContainer.Children
                        RefreshWidgetRecursive(lChild)
                    Next
                End If
                
                ' Special handling for Notebook widgets
                Dim lNotebook As Notebook = TryCast(vWidget, Notebook)
                If lNotebook IsNot Nothing Then
                    for i As Integer = 0 To lNotebook.NPages - 1
                        Dim lPage As Widget = lNotebook.GetNthPage(i)
                        If lPage IsNot Nothing Then
                            RefreshWidgetRecursive(lPage)
                        End If
                    Next
                End If
                
                ' Special handling for Paned widgets
                Dim lPaned As Paned = TryCast(vWidget, Paned)
                If lPaned IsNot Nothing Then
                    If lPaned.Child1 IsNot Nothing Then
                        RefreshWidgetRecursive(lPaned.Child1)
                    End If
                    If lPaned.Child2 IsNot Nothing Then
                        RefreshWidgetRecursive(lPaned.Child2)
                    End If
                End If
                
            Catch ex As Exception
                Console.WriteLine($"RefreshWidgetRecursive error: {ex.Message}")
            End Try

        End Sub

        ''' <summary>
        ''' Updates an existing custom theme with new values
        ''' </summary>
        ''' <summary>
        ''' Renames a custom theme in place, preserving all its color values
        ''' </summary>
        ''' <param name="vOldName">Current name of the theme to rename</param>
        ''' <param name="vNewName">New name for the theme</param>
        ''' <returns>True if renamed successfully, False if the theme isn't a custom theme, the new name is already taken, or the rename failed</returns>
        ''' <remarks>
        ''' Built-in themes (and the "System Colors" pseudo-theme) can never be renamed -
        ''' only themes present in pCustomThemes qualify, the same check DeleteTheme uses
        ''' </remarks>
        Public Function RenameTheme(vOldName As String, vNewName As String) As Boolean
            Try
                If String.IsNullOrWhiteSpace(vNewName) OrElse vOldName = vNewName Then Return False

                ' Cannot rename built-in themes
                Dim lTheme As EditorTheme = pCustomThemes.FirstOrDefault(Function(t) t.Name = vOldName)
                If lTheme Is Nothing Then Return False

                ' Can't collide with any existing theme name (built-in or custom)
                If pAvailableThemes.ContainsKey(vNewName) Then Return False

                Dim lThemesDir As String = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "SimpleIDE", "Themes")

                ' Rename on disk first - if this fails, nothing in memory has changed yet
                Dim lOldFilePath As String = System.IO.Path.Combine(lThemesDir, $"{vOldName}.json")
                Dim lNewFilePath As String = System.IO.Path.Combine(lThemesDir, $"{vNewName}.json")
                lTheme.Name = vNewName
                If Not SaveTheme(lTheme, lNewFilePath) Then
                    lTheme.Name = vOldName
                    Return False
                End If
                If File.Exists(lOldFilePath) Then
                    File.Delete(lOldFilePath)
                End If

                ' Re-key the in-memory collections
                pAvailableThemes.Remove(vOldName)
                pAvailableThemes(vNewName) = lTheme

                ' If the renamed theme was the active one, follow it under its new name
                If pCurrentTheme IsNot Nothing AndAlso pCurrentTheme.Name = vOldName Then
                    pSettingsManager.SetSetting("CurrentTheme", vNewName)
                End If

                RaiseEvent ThemeListChanged()
                Return True

            Catch ex As Exception
                Console.WriteLine($"RenameTheme error: {ex.Message}")
                Return False
            End Try
        End Function

        Public Sub UpdateCustomTheme(vThemeName As String, vUpdatedTheme As EditorTheme)
            Try
                If pAvailableThemes.ContainsKey(vThemeName) Then
                    ' Update the theme in the collection
                    vUpdatedTheme.Name = vThemeName  ' Ensure name is correct
                    pAvailableThemes(vThemeName) = vUpdatedTheme
                    
                    ' If it's the current theme, update it
                    If pCurrentTheme IsNot Nothing AndAlso pCurrentTheme.Name = vThemeName Then
                        pCurrentTheme = vUpdatedTheme
                        ApplyCurrentTheme()
                    End If
                End If
                
            Catch ex As Exception
                Console.WriteLine($"UpdateCustomTheme error: {ex.Message}")
            End Try
        End Sub


        
        ' Theme data class for JSON serialization
        Public Class ThemeData
            Public Property Name As String
            Public Property Description As String
            Public Property IsDarkTheme As Boolean
            Public Property BackgroundColor As String
            Public Property ForegroundColor As String
            Public Property SelectionColor As String
            Public Property CurrentLineColor As String
            Public Property LineNumberColor As String
            Public Property LineNumberBackgroundColor As String
            Public Property CurrentLineNumberColor As String
            Public Property CursorColor As String
            
            ' Tab colors (NEW)
            Public Property EditorBackgroundColor As String
            Public Property TabInactiveColor As String
            Public Property TabHoverColor As String
            Public Property AccentColor As String

            ' Status colors
            Public Property ErrorColor As String
            Public Property WarningColor As String
            Public Property InfoColor As String
            Public Property SuccessColor As String

            Public Property FontFamily As String
            Public Property FontSize As Integer
            Public Property SyntaxColors As Dictionary(Of String, String)
        End Class
        
    End Class
    
End Namespace
