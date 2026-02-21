# PERSONAL MODEL MANAGEMENT RULES

Planner: Gemini (Web)
Auditor: Gemini (AIStudio)
Manager: Claude Sonnet, Gemini Pro
Troubleshooter: GPT Codex, Gemini Pro
Developer: GPT-5 Mini

### 1. OPTIMIZING OUTPUT
* **For AI Studio (Reasoning Tasks):** Keep Temperature between **0.0 and 0.2** for logical consistency. Focused, logical, and less likely to "hallucinate" fake code. Good for fixing bugs or creating correct code.
* **For General Creative/Coding:** Temperature **1.0** is the default "sweet spot" for variety, innovation, or a more "human-like" tone. Fluid, creative, and better at conversational flow and brainstorming.

### 2. PREVENTING CONTEXT ROT & BULK
* **GPT-5-Mini Agents:** These models lose track of long-term goals. If the code starts getting messy, **"Flush"** the chat. Start a new session and re-paste the relevant `.md` file.
* **Modular Context:** Use the **"Vacuum Rule"**—only provide the specific technical details an AI needs for the current task to save context for reasoning.

### 3. MODULAR SPEC ARCHITECTURE
* **.github/copilot-instructions.md:** The "Who"—Contains the **AI Model Roles & Boundaries**. Governs VS Code models automatically. Tells models to reference OVERVIEW_MOUSE_APP.md for project identity
* **OVERVIEW.md:** Contains the "What & Why"—User perspective, "Slick" UI rules, and AI Roles. Feed this to **EVERY** model to maintain the app's identity.
* **IMPLEMENTATION.md:** Contains the "How"—Data models, property names (SensMultiplier, etc.), and complex logic. Only feed this to models touching the "nervous system" of the app.


### 4. TROUBLESHOOTING ESCALATION
* **Step 1:** Use **VS Code Gemini** (it sees the local files/errors).
* **Step 2:** If the error persists, use **Web Gemini** to brainstorm a new approach and generate a new implementation prompt.
* **Step 3:** If the logic is fundamentally broken, use **AI Studio** to audit the entire script using the full `IMPLEMENTATION.md`.

### 5. SPEC-DRIVEN DEVELOPMENT
* **Naming Consistency:** Never let a model "guess" a variable name. Always reference the property lists in `IMPLEMENTATION.md` when starting a new feature prompt to ensure naming consistency across the project.