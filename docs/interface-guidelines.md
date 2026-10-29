# Interface behavior

The home page puts the selected game or window first, followed immediately by the user's ordered quick-access actions. Open apps, audio controls and session utilities remain available below that primary flow. Returning to the game takes one deliberate activation.

Routine results use a single quiet line. Completed, cancelled and simulated acknowledgements fade after four seconds; accepted requests remain explicitly requested and fade after six seconds. These messages do not add navigation stops or take focus. The full result remains available under **System → Last result**. Pending observations and uncertain or failed results remain readable with details and dismissal actions. The reserved notice line prevents content from jumping when a message appears or expires.

Instructions follow the most recent input. Controller navigation and Guide activation show gamepad prompts; keyboard input shows keys; clicks, wheel scrolling and deliberate pointer movement show pointer instructions. Small pointer jitter and layout movement do not change modes. Changing prompts updates existing controls rather than rebuilding sliders, moving keyboard focus or changing the selected action.

The overlay combines the destination monitor's usable work area, the Windows DPI reported for its own window, an automatic size adjustment and the user's size preference. TV and desktop profiles share this calculation. Display, DPI and work-area changes refresh placement. A width cap must not squeeze an otherwise fitting panel below its supported content width.

Application copy and accessibility descriptions use English. The application culture is English even when Windows uses another language; this does not change Windows locale settings.

Review screenshots in dark and light themes, at normal and compact widths, with controller and pointer instructions and both routine and uncertain results. Reviewers must see the rendered interface. Automated coverage also checks result expiry, retained messages, unchanged focus and controls when switching input, primary action order, and monitor geometry across DPI values.
