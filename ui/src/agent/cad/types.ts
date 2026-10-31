/**
 * Everything the system prompt needs to know about the CAD application the agent is integrated into. The core prompt
 * (agent/systemPrompt.ts) is CAD-neutral and pulls names, terms and knowledge from here, so supporting another
 * application (Fusion, Inventor, NX, CATIA...) means writing another profile - not editing the core.
 *
 * Every section is markdown that goes into the prompt as-is: keep it to facts about this application and its API.
 */
export interface CadProfile {
  /** "SolidWorks" */
  name: string;
  /** How the role line describes the agent's expertise: "SolidWorks API programmer". */
  expertise: string;
  /** The add-in tool that runs code against the CAD API, and its language. */
  code: { tool: string; language: string };
  /** The tag of the context block the add-in appends to each user message. */
  contextTag: string;
  /** Names of the application's UI concepts, as the user knows them. */
  terms: {
    /** The page a tool shows for its inputs: "PropertyManager page". */
    inputsPage: string;
    /** Where tools appear: 'the "SolidWorks Agent" ribbon tab'. */
    toolbar: string;
    /** How the user assigns hotkeys. */
    hotkeys: string;
    /** The feature list: "feature tree". */
    featureTree: string;
  };
  sections: {
    /** How the agent acts on the application: the code tool's environment, the other add-in tools. */
    host: string;
    /** API conventions: units, casting, selection, sketches, signatures. */
    api: string;
    /** The application's own display commands (isolate, show/hide...). */
    display: string;
    /**
     * General modelling knowledge: how the application behaves, its limits and well-known pitfalls (multi-body
     * features, fillet order, stale references, configurations...). The "knowledge bank".
     */
    knowledge: string;
    /** What a tool's macro and preview code look like here: the Inputs API, temporary bodies for previews. */
    toolCode: string;
    /** API calls that can crash the application, and how a crash is investigated. */
    crashSafety: string;
    /** The application's library of standard parts, if it has one ("" if not). */
    library: string;
    /** Materials and appearances, used when the user asks for realistic parts. */
    materials: string;
  };
}
