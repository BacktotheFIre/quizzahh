using System.Collections.Generic;

static class Bank
{
    static List<Q> questions;

    public static List<Q> Load()
    {
        questions = new List<Q>();
        LoadQuestions();
        return questions;
    }

    static void Add(string topic, string text, int answer, string why, params string[] options)
    {
        questions.Add(new Q { Topic = topic, Text = text, Answer = answer, Why = why, Options = options });
    }

    static void LoadQuestions()
    {
        // Data Structures
        Add("Data Structures", "Which data structure follows Last-In, First-Out (LIFO) order?", 1,
            "A stack is LIFO: the last item pushed is the first one popped, like a stack of plates. A queue is the opposite (FIFO).",
            "Queue", "Stack", "Linked list", "Heap");
        Add("Data Structures", "What is the average time complexity to look up a key in a hash table?", 0,
            "A hash function maps the key straight to a bucket, so lookup is O(1) on average. Worst case (many collisions) degrades to O(n).",
            "O(1)", "O(log n)", "O(n)", "O(n log n)");
        Add("Data Structures", "In a binary search tree, where are values smaller than a node stored?", 1,
            "The BST property: everything in the left subtree is smaller than the node and everything in the right subtree is larger. That ordering is what makes searching fast.",
            "In its right subtree", "In its left subtree", "In its parent", "Anywhere in the tree");
        Add("Data Structures", "What is the time complexity of accessing the element at index i in an array?", 2,
            "Arrays are stored contiguously, so the address of element i is just base + i × element_size. That's a single calculation: O(1).",
            "O(n)", "O(log n)", "O(1)", "O(i²)");
        Add("Data Structures", "Which data structure is typically used to implement a priority queue?", 1,
            "A binary heap keeps the highest (or lowest) priority element at the root, giving O(1) peek and O(log n) insert/remove.",
            "Stack", "Heap", "Hash table", "Doubly linked list");
        Add("Data Structures", "What does each node in a singly linked list contain?", 1,
            "A singly linked list node holds data and one pointer to the next node. A doubly linked list adds a pointer to the previous node too.",
            "A value and pointers to the next and previous nodes", "A value and a pointer to the next node", "Only a value", "A key and a hash");
        Add("Data Structures", "A graph where edges have a direction (A → B is different from B → A) is called:", 1,
            "In a directed graph (digraph), each edge points one way. 'Weighted' means edges carry costs, which is a separate property.",
            "A weighted graph", "A directed graph", "A complete graph", "A bipartite graph");
        Add("Data Structures", "What is the maximum number of children a node can have in a binary tree?", 1,
            "'Binary' means two: each node has at most a left child and a right child.",
            "1", "2", "3", "Unlimited");

        // Algorithms
        Add("Algorithms", "What is the worst-case time complexity of binary search on a sorted array?", 1,
            "Binary search halves the search space each step, so it takes about log₂(n) steps. 1,000,000 items need only ~20 comparisons.",
            "O(n)", "O(log n)", "O(1)", "O(n log n)");
        Add("Algorithms", "What is the average time complexity of merge sort?", 2,
            "Merge sort splits the list in half log n times, and merging at each level costs O(n), giving O(n log n) in every case.",
            "O(n²)", "O(n)", "O(n log n)", "O(log n)");
        Add("Algorithms", "What is the worst-case time complexity of quicksort?", 1,
            "If the pivot is always the smallest or largest element (e.g. already-sorted input with a naive pivot), each partition only removes one element, giving O(n²). Its average case is O(n log n).",
            "O(n log n)", "O(n²)", "O(n)", "O(log n)");
        Add("Algorithms", "Which algorithm finds the shortest path from one node to all others in a graph with non-negative edge weights?", 1,
            "Dijkstra's algorithm repeatedly picks the closest unvisited node and relaxes its edges. It requires non-negative weights; Bellman-Ford handles negative ones. Kruskal's builds a minimum spanning tree, not shortest paths.",
            "Depth-first search", "Dijkstra's algorithm", "Kruskal's algorithm", "Bubble sort");
        Add("Algorithms", "Breadth-first search (BFS) uses which data structure to track nodes to visit?", 1,
            "BFS visits nodes level by level, so it uses a FIFO queue. Depth-first search (DFS) uses a stack (or recursion).",
            "Stack", "Queue", "Heap", "Hash table");
        Add("Algorithms", "What technique solves problems by storing results of overlapping subproblems to avoid recomputing them?", 2,
            "Dynamic programming caches subproblem answers (memoization or tabulation). Computing Fibonacci with DP is O(n) instead of exponential.",
            "Greedy algorithms", "Divide and conquer", "Dynamic programming", "Brute force");
        Add("Algorithms", "What does Big-O notation describe?", 1,
            "Big-O describes the growth rate (an upper bound) of time or space as n grows, ignoring constants. It's about scaling, not exact seconds.",
            "The exact runtime in seconds", "An upper bound on how runtime or memory grows with input size", "The amount of code in an algorithm", "The best-case performance only");
        Add("Algorithms", "What is a recursive function's 'base case'?", 1,
            "The base case is the condition where the function returns without calling itself. Without it, recursion never ends and you get a stack overflow.",
            "The first line of the function", "The condition that stops the recursion", "The most expensive call", "The function's return type");
        Add("Algorithms", "What is the time complexity of bubble sort in the worst case?", 2,
            "Bubble sort compares adjacent pairs and makes up to n passes over n elements: n × n = O(n²).",
            "O(n)", "O(n log n)", "O(n²)", "O(2ⁿ)");

        // Programming
        Add("Programming", "In object-oriented programming, what is 'encapsulation'?", 1,
            "Encapsulation keeps an object's internal state private and exposes it only through methods. Inheritance and polymorphism are the other two options described.",
            "One class inheriting from another", "Bundling data with the methods that operate on it and hiding internal details", "Using the same method name for different behaviors", "Converting one type to another");
        Add("Programming", "What is 'polymorphism' in OOP?", 0,
            "Polymorphism lets one interface have many implementations. For example, calling speak() on a Dog and a Cat runs different code through the same call.",
            "Objects of different classes responding to the same method call in their own way", "A class having many variables", "Writing code in many languages", "Hiding data inside a class");
        Add("Programming", "What is the difference between a compiler and an interpreter?", 1,
            "Compilers (like for C) translate everything ahead of time into an executable. Interpreters (like classic Python) read and run code statement by statement. Many modern languages mix both (e.g. JIT compilation).",
            "There is no difference", "A compiler translates the whole program to machine code before running; an interpreter executes code line by line", "An interpreter is always faster", "A compiler only works with Python");
        Add("Programming", "What does a 'null pointer exception' usually mean?", 1,
            "It happens when you call a method or access a field on a variable that is null/None, meaning it doesn't refer to any actual object.",
            "The program ran out of memory", "Code tried to use a reference that points to nothing", "A number was divided by zero", "A file could not be found");
        Add("Programming", "Which of these is an immutable type in Python?", 2,
            "Tuples can't be changed after creation. Lists, dicts and sets can be modified in place, so they are mutable.",
            "list", "dict", "tuple", "set");
        Add("Programming", "What does 'git commit' do?", 1,
            "A commit records staged changes in your local repo. 'git push' is what uploads commits to a remote like GitHub; 'git pull' downloads.",
            "Uploads code to GitHub", "Saves a snapshot of staged changes to the local repository history", "Downloads changes from a remote", "Deletes a branch");
        Add("Programming", "What is a 'race condition'?", 1,
            "When two threads read and write shared data without coordination, the outcome depends on who runs first. Locks/mutexes are a common fix.",
            "When a program runs too fast", "A bug where the result depends on the unpredictable timing of multiple threads or processes", "A loop that never ends", "A competition between compilers");
        Add("Programming", "In most languages, what does the expression 7 % 3 evaluate to?", 1,
            "% is the modulo (remainder) operator. 7 ÷ 3 = 2 with remainder 1, so 7 % 3 = 1.",
            "2", "1", "2.33", "21");
        Add("Programming", "What is the purpose of a function's 'return' statement?", 1,
            "return ends the function and hands a value back to the code that called it. Printing just displays something; the caller can't use a printed value.",
            "To print a value to the screen", "To send a value back to the caller and exit the function", "To restart the function", "To import a library");

        // Networking
        Add("Networking", "What does DNS do?", 1,
            "DNS (Domain Name System) is the internet's phone book: it turns human-friendly names into the numeric IP addresses computers use. Assigning IPs is DHCP's job.",
            "Encrypts web traffic", "Translates domain names (like google.com) into IP addresses", "Assigns IP addresses to devices", "Blocks malicious websites");
        Add("Networking", "What is the main difference between TCP and UDP?", 1,
            "TCP sets up a connection, acknowledges packets and retransmits lost ones. UDP just sends packets with no guarantees, which is faster and fine for video calls or games where speed beats perfection.",
            "TCP is faster than UDP", "TCP guarantees ordered, reliable delivery; UDP doesn't", "UDP is only used for email", "There is no difference");
        Add("Networking", "Which port does HTTPS use by default?", 2,
            "HTTPS uses port 443. HTTP is 80, FTP is 21, and SSH is 22.",
            "80", "21", "443", "22");
        Add("Networking", "How many bits are in an IPv4 address?", 1,
            "IPv4 addresses are 32 bits, written as four 8-bit numbers (e.g. 192.168.1.1). IPv6 addresses are 128 bits.",
            "16", "32", "64", "128");
        Add("Networking", "What protocol automatically assigns IP addresses to devices on a network?", 1,
            "DHCP (Dynamic Host Configuration Protocol) hands out IP addresses, gateway and DNS settings when a device joins a network.",
            "DNS", "DHCP", "FTP", "SMTP");
        Add("Networking", "What does an HTTP 404 status code mean?", 1,
            "404 means the server couldn't find the requested resource. 200 is success, 401 is unauthorized, and 500 is a server error.",
            "Server error", "Not found", "Unauthorized", "Success");
        Add("Networking", "A MAC address identifies:", 1,
            "A MAC address is a hardware identifier burned into a network interface card, used for delivery on the local network. It has nothing to do with Apple Macs.",
            "A website", "A network interface's hardware", "A user's account", "An Apple computer only");
        Add("Networking", "Which layer of the OSI model is responsible for routing packets between networks?", 2,
            "The Network layer (Layer 3) handles logical addressing and routing, which is where IP and routers live. Layer 2 deals with local delivery via MAC addresses.",
            "Physical (Layer 1)", "Data Link (Layer 2)", "Network (Layer 3)", "Application (Layer 7)");

        // Operating Systems
        Add("Operating Systems", "What is the difference between a process and a thread?", 1,
            "A process is a running program with its own isolated memory. Threads are lighter-weight units of execution inside a process that share its memory, which makes communication easy but needs synchronization.",
            "They are the same thing", "A process has its own memory space; threads within a process share memory", "Threads have their own memory; processes share", "A thread is a type of file");
        Add("Operating Systems", "What is 'virtual memory'?", 1,
            "Virtual memory maps each process's addresses to physical RAM through page tables, and can swap rarely used pages to disk. This isolates processes and lets programs use more memory than physically exists.",
            "Memory in the cloud", "A technique that gives each process the illusion of a large private address space, using disk as overflow", "RAM that is faster than normal", "Memory used only by virtual machines");
        Add("Operating Systems", "What is a 'deadlock'?", 1,
            "Example: thread A holds lock 1 and wants lock 2, while thread B holds lock 2 and wants lock 1. Both wait forever.",
            "When the CPU overheats", "When two or more processes each wait for a resource the other holds, so none can proceed", "When a program finishes", "When memory is full");
        Add("Operating Systems", "What is the 'kernel' of an operating system?", 1,
            "The kernel runs with full hardware privileges and handles scheduling, memory management, device drivers and system calls. Apps request services from it.",
            "The graphical desktop", "The core part that manages hardware, memory and processes", "A web browser", "The boot screen");
        Add("Operating Systems", "What does a CPU scheduler do?", 1,
            "The scheduler switches between runnable tasks (context switching) so many programs appear to run at once, using policies like round-robin or priority scheduling.",
            "Schedules meetings", "Decides which process or thread runs on the CPU next", "Cleans the hard drive", "Installs updates");
        Add("Operating Systems", "What is a 'page fault'?", 1,
            "When a process touches a virtual page not currently in physical memory, the CPU raises a page fault and the OS loads the page (often from disk). It's normal, not necessarily an error.",
            "A corrupted web page", "When a program accesses memory that isn't currently loaded in RAM", "A printer error", "A bug in the kernel");

        // Databases
        Add("Databases", "What does SQL stand for?", 0,
            "SQL = Structured Query Language, the standard language for querying and managing relational databases.",
            "Structured Query Language", "Simple Question Language", "Sequential Query Logic", "System Quality Language");
        Add("Databases", "What is a primary key?", 1,
            "A primary key must be unique and not null for every row, e.g. a user ID. It lets other tables reference rows reliably.",
            "A password for the database", "A column (or set of columns) that uniquely identifies each row in a table", "The first column in any table", "A key used to encrypt data");
        Add("Databases", "What is a foreign key?", 1,
            "A foreign key links tables together, e.g. orders.customer_id refers to customers.id, and the database can enforce that the referenced row exists.",
            "A key from another country", "A column that references the primary key of another table", "A duplicate primary key", "An encrypted column");
        Add("Databases", "Which SQL clause filters rows?", 1,
            "WHERE keeps only rows matching a condition, e.g. WHERE age > 18. ORDER BY sorts, GROUP BY groups, and SELECT chooses columns.",
            "ORDER BY", "WHERE", "GROUP BY", "SELECT");
        Add("Databases", "What does the 'A' in ACID stand for?", 1,
            "Atomicity means a transaction is all-or-nothing: either every step succeeds or none are applied. ACID = Atomicity, Consistency, Isolation, Durability.",
            "Availability", "Atomicity", "Accuracy", "Authentication");
        Add("Databases", "What is the main purpose of a database index?", 1,
            "An index (often a B-tree) lets the database find rows without scanning the entire table, much like a book's index. The tradeoff is extra storage and slower writes.",
            "To back up data", "To speed up lookups on certain columns", "To encrypt the table", "To list all tables");
        Add("Databases", "Which type of JOIN returns only rows that have matching values in both tables?", 2,
            "INNER JOIN keeps only matches. LEFT JOIN keeps every row from the left table even without a match; CROSS JOIN pairs every row with every row.",
            "LEFT JOIN", "FULL OUTER JOIN", "INNER JOIN", "CROSS JOIN");

        // Architecture
        Add("Architecture", "How many bits are in a byte?", 1,
            "A byte is 8 bits, which can represent 256 different values (0–255). Four bits is called a nibble.",
            "4", "8", "16", "32");
        Add("Architecture", "What is the binary number 1011 in decimal?", 2,
            "1011 = 1×8 + 0×4 + 1×2 + 1×1 = 11.",
            "9", "10", "11", "13");
        Add("Architecture", "What is the hexadecimal value FF in decimal?", 2,
            "F = 15, so FF = 15×16 + 15 = 255. That's the largest value one byte can hold.",
            "15", "100", "255", "256");
        Add("Architecture", "What is CPU cache?", 1,
            "Cache (L1, L2, L3) sits between the CPU and RAM. Since RAM is slow by comparison, keeping frequently used data in cache greatly speeds things up.",
            "Saved web pages", "Small, very fast memory close to the CPU that stores recently used data", "The hard drive", "A type of virus");
        Add("Architecture", "Which type of memory loses its contents when power is turned off?", 2,
            "RAM is volatile: it needs power to keep data. SSDs, hard drives and ROM are non-volatile and keep data without power.",
            "SSD", "ROM", "RAM", "Hard disk");
        Add("Architecture", "What does the ALU in a CPU do?", 1,
            "The Arithmetic Logic Unit does math (add, subtract) and logic (AND, OR, compare). The control unit tells it what to do.",
            "Stores files", "Performs arithmetic and logic operations", "Connects to the internet", "Controls the monitor");
        Add("Architecture", "In two's complement with 8 bits, what does 11111111 represent?", 1,
            "In two's complement, all 1s is -1. (As an unsigned number it would be 255, but two's complement is how signed integers are stored.)",
            "255", "-1", "-127", "0");

        // Security
        Add("Security", "What is SQL injection?", 1,
            "If user input is pasted directly into SQL, an attacker can type something like ' OR 1=1 -- to change the query. The fix is parameterized queries / prepared statements.",
            "Adding SQL to a resume", "An attack where malicious input is inserted into a database query", "A way to speed up queries", "Installing a database");
        Add("Security", "Why should passwords be stored as hashes rather than plain text?", 1,
            "A hash is one-way, so the site can verify a password without storing it. Use slow, salted algorithms like bcrypt or Argon2 so leaked hashes are hard to crack.",
            "Hashes take less space", "So that if the database leaks, attackers don't get the actual passwords", "Hashes are easier to read", "It's required by HTML");
        Add("Security", "In public-key cryptography, which key is shared openly?", 1,
            "Anyone can use your public key to encrypt messages to you or verify your signatures, but only your private key, which you keep secret, can decrypt or sign.",
            "The private key", "The public key", "Both keys", "Neither key");
        Add("Security", "What is phishing?", 1,
            "Phishing is social engineering: fake messages that look legit and try to get you to enter passwords or click malicious links.",
            "A firewall technique", "Tricking people into revealing information, often through fake emails or websites", "A type of encryption", "Scanning network ports");
        Add("Security", "What does two-factor authentication (2FA) add?", 1,
            "2FA combines something you know (password) with something you have (phone/key) or are (fingerprint). Stealing one factor alone isn't enough.",
            "A second password of the same type", "A second, different kind of proof of identity, like a code from your phone", "Faster logins", "Encryption of your hard drive");
        Add("Security", "What is the difference between encryption and hashing?", 1,
            "Encrypted data can be decrypted with the right key. A hash can't be turned back into the original; you can only hash a guess and compare.",
            "No difference", "Encryption is reversible with a key; hashing is one-way", "Hashing is reversible; encryption is not", "Both are only for passwords");

        // Theory
        Add("Theory", "What does it mean if a problem is 'NP-complete'?", 1,
            "NP-complete problems (like the Traveling Salesman decision problem or SAT) are the 'hardest' in NP. A polynomial solution to one would solve them all, which would prove P = NP.",
            "It can't be solved at all", "It's in NP and every NP problem can be reduced to it; no known fast (polynomial) solution exists", "It's always solved in O(1)", "It only applies to networking");
        Add("Theory", "What is the Halting Problem?", 1,
            "Alan Turing proved in 1936 that no general algorithm can determine whether every possible program halts. It's a foundational result about the limits of computation.",
            "Stopping a running program with Ctrl+C", "The proven impossibility of writing a program that decides, for every program and input, whether it eventually stops", "A CPU overheating issue", "A sorting algorithm");
        Add("Theory", "What is a Turing machine?", 1,
            "It's an abstract model: an infinite tape, a read/write head and state transitions. Anything a modern computer can compute, a Turing machine can too, given enough time.",
            "A real computer built by Alan Turing", "A theoretical model of computation with a tape, a head and a set of rules", "A machine for breaking codes", "A type of CPU");
        Add("Theory", "Which Boolean operation returns true only when exactly one input is true?", 2,
            "XOR (exclusive or) is true when the inputs differ. OR is also true when both are true; AND only when both are true.",
            "AND", "OR", "XOR", "NAND");
        Add("Theory", "A regular expression can be recognized by which simple machine?", 0,
            "Regular languages are exactly those recognized by finite automata (DFAs/NFAs). Pushdown automata handle the larger class of context-free languages, such as balanced parentheses.",
            "Finite automaton", "Turing machine only", "Pushdown automaton only", "None");
    }
}